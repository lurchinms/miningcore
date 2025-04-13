using System;
using static System.Array;
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autofac;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Kaspa.Custom.Karlsencoin;
using Miningcore.Blockchain.Kaspa.Custom.Pyrin;
using NLog;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using kaspaWalletd = Miningcore.Blockchain.Kaspa.KaspaWalletd;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

public class KaspaJobManager : JobManagerBase<KaspaJob>
{
    public KaspaJobManager(
        IComponentContext ctx,
        IMessageBus messageBus,
        IMasterClock clock,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, messageBus)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(extraNonceProvider);

        this.clock = clock;
        this.extraNonceProvider = extraNonceProvider;
    }
    
    private DaemonEndpointConfig[] daemonEndpoints;
    private DaemonEndpointConfig[] walletDaemonEndpoints;
    private KaspaCoinTemplate coin;
    private kaspad.KaspadRPC.KaspadRPCClient rpc;
    private kaspaWalletd.KaspaWalletdRPC.KaspaWalletdRPCClient walletRpc;
    private string network;
    private readonly List<KaspaJob> validJobs = new();
    private readonly IExtraNonceProvider extraNonceProvider;
    private readonly IMasterClock clock;
    private KaspaPoolConfigExtra extraPoolConfig;
    private KaspaPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    protected int maxActiveJobs;
    protected string extraData;
    protected IHashAlgorithm customBlockHeaderHasher;
    protected IHashAlgorithm customCoinbaseHasher;
    protected IHashAlgorithm customShareHasher;
    
    protected IObservable<kaspad.RpcBlock> KaspaSubscribeNewBlockTemplate(CancellationToken ct, object payload = null,
        JsonSerializerSettings payloadJsonSerializerSettings = null)
    {
        return Observable.Defer(() => Observable.Create<kaspad.RpcBlock>(obs =>
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Task.Run(async () =>
            {
                using(cts)
                {
                    retry_subscription:
                        // we need a stream to communicate with Kaspad
                        var streamNotifyNewBlockTemplate = rpc.MessageStream(null, null, cts.Token);

                        // we need a request for subscribing to NotifyNewBlockTemplate
                        var requestNotifyNewBlockTemplate = new kaspad.KaspadMessage();
                        requestNotifyNewBlockTemplate.NotifyNewBlockTemplateRequest = new kaspad.NotifyNewBlockTemplateRequestMessage();

                        // we need a request for retrieving BlockTemplate
                        var requestBlockTemplate = new kaspad.KaspadMessage();
                        requestBlockTemplate.GetBlockTemplateRequest = new kaspad.GetBlockTemplateRequestMessage
                        {
                            PayAddress = poolConfig.Address,
                            ExtraData = extraData,
                        };

                        logger.Debug(() => $"Sending NotifyNewBlockTemplateRequest");

                        try
                        {
                            await streamNotifyNewBlockTemplate.RequestStream.WriteAsync(requestNotifyNewBlockTemplate, cts.Token);
                        }
                        catch(Exception ex)
                        {
                            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while subscribing to kaspad \"NewBlockTemplate\" notifications");

                            if(!cts.IsCancellationRequested)
                            {
                                // We make sure the stream is closed in order to free resources and avoid reaching the "RPC inbound connections limitation"
                                await streamNotifyNewBlockTemplate.RequestStream.CompleteAsync();
                                logger.Error(() => $"Reconnecting in 10s");
                                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                                goto retry_subscription;
                            }
                            else
                                goto end_gameover;
                        }

                        while (!cts.IsCancellationRequested)
                        {
                            logger.Debug(() => $"Successful `NewBlockTemplate` subscription");

                            retry_blocktemplate:
                                logger.Debug(() => $"New job received :D");

                                try
                                {
                                    await streamNotifyNewBlockTemplate.RequestStream.WriteAsync(requestBlockTemplate, cts.Token);
                                    await foreach (var responseBlockTemplate in streamNotifyNewBlockTemplate.ResponseStream.ReadAllAsync(cts.Token))
                                    {
                                        logger.Debug(() => $"DaaScore (BlockHeight): {responseBlockTemplate.GetBlockTemplateResponse.Block.Header.DaaScore}");

                                        // publish
                                        //logger.Debug(() => $"Publishing...");
                                        obs.OnNext(responseBlockTemplate.GetBlockTemplateResponse.Block);

                                        if(!string.IsNullOrEmpty(responseBlockTemplate.GetBlockTemplateResponse.Error?.Message))
                                            logger.Warn(() => responseBlockTemplate.GetBlockTemplateResponse.Error?.Message);
                                    }
                                }
                                catch(NullReferenceException)
                                {
                                    // The following is weird but correct, when all data has been received `streamNotifyNewBlockTemplate.ResponseStream.ReadAllAsync()` will return a `NullReferenceException`
                                    logger.Debug(() => $"Waiting for data...");
                                    goto retry_blocktemplate;
                                }

                                catch(Exception ex)
                                {
                                    logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while streaming kaspad \"NewBlockTemplate\" notifications");

                                    if(!cts.IsCancellationRequested)
                                    {
                                        // We make sure the stream is closed in order to free resources and avoid reaching the "RPC inbound connections limitation"
                                        await streamNotifyNewBlockTemplate.RequestStream.CompleteAsync();
                                        logger.Error(() => $"Reconnecting in 10s");
                                        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                                        goto retry_subscription;
                                    }
                                    else
                                        goto end_gameover;
                                }
                        }
                        
                        end_gameover:
                            // We make sure the stream is closed in order to free resources and avoid reaching the "RPC inbound connections limitation"
                            await streamNotifyNewBlockTemplate.RequestStream.CompleteAsync();
                            logger.Debug(() => $"No more data received. Bye!");
                }
            }, cts.Token);
            
            return Disposable.Create(() => { cts.Cancel(); });
        }));
    }
    
    private void SetupJobUpdates(CancellationToken ct)
    {
        var blockFound = blockFoundSubject.Synchronize();
        var pollTimerRestart = blockFoundSubject.Synchronize();

        var triggers = new List<IObservable<(string Via, kaspad.RpcBlock Data)>>
        {
            blockFound.Select(_ => (JobRefreshBy.BlockFound, (kaspad.RpcBlock) null))
        };

        // Listen to kaspad "NewBlockTemplate" notifications
        var getWorkKaspad = KaspaSubscribeNewBlockTemplate(ct)
            .Publish()
            .RefCount();
            
        triggers.Add(getWorkKaspad
            .Select(blockTemplate => (JobRefreshBy.BlockTemplateStream, blockTemplate))
            .Publish()
            .RefCount());

        // get initial blocktemplate
        triggers.Add(Observable.Interval(TimeSpan.FromMilliseconds(1000))
            .Select(_ => (JobRefreshBy.Initial, (kaspad.RpcBlock) null))
            .TakeWhile(_ => !hasInitialBlockTemplate));

        Jobs = triggers.Merge()
            .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Via, x.Data)))
            .Concat()
            .Where(x => x)
            .Do(x =>
            {
                if(x)
                    hasInitialBlockTemplate = true;
            })
            .Select(x => GetJobParamsForStratum())
            .Publish()
            .RefCount();
    }
    
    private KaspaJob CreateJob(long blockHeight)
    {
        switch(coin.Symbol)
        {
            case "CAS":
            case "HTN":
                if(customBlockHeaderHasher is not Blake3IHash)
                {
                    string coinbaseBlockHash = KaspaConstants.CoinbaseBlockHash;
                    byte[] hashBytes = Encoding.UTF8.GetBytes(coinbaseBlockHash.PadRight(32, '\0')).Take(32).ToArray();
                    customBlockHeaderHasher = new Blake3IHash(hashBytes);
                }

                if(customCoinbaseHasher is not Blake3IHash)
                        customCoinbaseHasher = new Blake3IHash();

                if(customShareHasher is not Blake3IHash)
                    customShareHasher = new Blake3IHash();

                return new PyrinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);
            case "KODA":
                if(customBlockHeaderHasher is not Blake3IHash)
                {
                    string coinbaseBlockHash = KaspaConstants.CoinbaseBlockHash;
                    byte[] hashBytes = Encoding.UTF8.GetBytes(coinbaseBlockHash.PadRight(32, '\0')).Take(32).ToArray();
                    customBlockHeaderHasher = new Blake3IHash(hashBytes);
                }

                if(customCoinbaseHasher is not Blake3IHash)
                        customCoinbaseHasher = new Blake3IHash();

                if(customShareHasher is not Blake3IHash)
                    customShareHasher = new Blake3IHash();

                return new PyrinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);
            case "KLS":
                var karlsenNetwork = network.ToLower();

                if(customBlockHeaderHasher is not Blake2b)
                    customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                if(customCoinbaseHasher is not Blake3IHash)
                    customCoinbaseHasher = new Blake3IHash();

                if(karlsenNetwork == "testnet" && blockHeight >= KarlsencoinConstants.FishHashPlusForkHeightTestnet)
                {
                    logger.Debug(() => $"fishHashPlusHardFork activated");

                    if(customShareHasher is not FishHashKarlsen)
                    {
                        var started = DateTime.Now;
                        logger.Debug(() => $"Generating light cache");

                        customShareHasher = new FishHashKarlsen(true);

                        logger.Debug(() => $"Done generating light cache after {DateTime.Now - started}");
                    }
                }
                else if(karlsenNetwork == "testnet" && blockHeight >= KarlsencoinConstants.FishHashForkHeightTestnet)
                {
                    logger.Debug(() => $"fishHashHardFork activated");

                    if(customShareHasher is not FishHashKarlsen)
                    {
                        var started = DateTime.Now;
                        logger.Debug(() => $"Generating light cache");

                        customShareHasher = new FishHashKarlsen();

                        logger.Debug(() => $"Done generating light cache after {DateTime.Now - started}");
                    }
                }
                else
                    if(customShareHasher is not CShake256)
                        customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));

                return new KarlsencoinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);
            case "CSS":
            case "PUG":
            case "NTL":
            case "NXL":
                if(customBlockHeaderHasher is not Blake2b)
                    customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                if(customCoinbaseHasher is not Blake3IHash)
                    customCoinbaseHasher = new Blake3IHash();

                if(customShareHasher is not CShake256)
                    customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));

                return new KarlsencoinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);
            case "PYI":
                if(blockHeight >= PyrinConstants.Blake3ForkHeight)
                {
                    logger.Debug(() => $"blake3HardFork activated");

                    if(customBlockHeaderHasher is not Blake3IHash)
                    {
                        string coinbaseBlockHash = KaspaConstants.CoinbaseBlockHash;
                        byte[] hashBytes = Encoding.UTF8.GetBytes(coinbaseBlockHash.PadRight(32, '\0')).Take(32).ToArray();
                        customBlockHeaderHasher = new Blake3IHash(hashBytes);
                    }

                    if(customCoinbaseHasher is not Blake3IHash)
                        customCoinbaseHasher = new Blake3IHash();

                    if(customShareHasher is not Blake3IHash)
                        customShareHasher = new Blake3IHash();
                }
                else
                {
                    if(customBlockHeaderHasher is not Blake2b)
                        customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                    if(customCoinbaseHasher is not CShake256)
                        customCoinbaseHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseProofOfWorkHash));

                    if(customShareHasher is not CShake256)
                        customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));
                }

                return new PyrinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);
        }
        
        if(customBlockHeaderHasher is not Blake2b)
            customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

        if(customCoinbaseHasher is not CShake256)
            customCoinbaseHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseProofOfWorkHash));

        if(customShareHasher is not CShake256)
            customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHashs2def.h"





























#pragma once
#line 32 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"








#line 41 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"


extern "C" {
#line 45 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"



#line 49 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

#pragma warning(push)
#pragma warning(disable:4201)
#pragma warning(disable:4214) 







#line 61 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"







#line 69 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

#line 71 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

#line 1 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\inaddr.h"















#pragma once

#pragma region Desktop Family or OneCore Family or Games Family






typedef struct in_addr {
        union {
                struct { UCHAR s_b1,s_b2,s_b3,s_b4; } S_un_b;
                struct { USHORT s_w1,s_w2; } S_un_w;
                ULONG S_addr;
        } S_un;






} IN_ADDR, *PIN_ADDR,  *LPIN_ADDR;

#line 40 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\inaddr.h"
#pragma endregion

#line 43 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\inaddr.h"
#line 73 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"



#pragma region Desktop Family or OneCore Family or Games Family




typedef USHORT ADDRESS_FAMILY;
#line 83 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#pragma endregion













































#line 130 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"







#line 138 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"



#line 142 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"



#line 146 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"


#line 149 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#line 150 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#line 151 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

#line 153 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"





















































                                    
                                    
                                    






                                    

                                    

                                    

#line 222 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"













#pragma region Desktop Family or OneCore Family or Games Family




typedef struct sockaddr {



#line 245 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
    ADDRESS_FAMILY sa_family;           
#line 247 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

    CHAR sa_data[14];                   
} SOCKADDR, *PSOCKADDR,  *LPSOCKADDR;








typedef struct _SOCKET_ADDRESS {
      LPSOCKADDR lpSockaddr;








    INT iSockaddrLength;
} SOCKET_ADDRESS, *PSOCKET_ADDRESS, *LPSOCKET_ADDRESS;
#line 271 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#pragma endregion




typedef struct _SOCKET_ADDRESS_LIST {
    INT             iAddressCount;
    SOCKET_ADDRESS  Address[1];
} SOCKET_ADDRESS_LIST, *PSOCKET_ADDRESS_LIST,  *LPSOCKET_ADDRESS_LIST;







#line 288 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"




typedef struct _CSADDR_INFO {
    SOCKET_ADDRESS LocalAddr ;
    SOCKET_ADDRESS RemoteAddr ;
    INT iSocketType ;
    INT iProtocol ;
} CSADDR_INFO, *PCSADDR_INFO,  * LPCSADDR_INFO ;
#line 299 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"























#line 323 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

typedef struct sockaddr_storage {
    ADDRESS_FAMILY ss_family;      

    CHAR __ss_pad1[((sizeof(__int64)) - sizeof(USHORT))];  
                                   
                                   
                                   
    __int64 __ss_align;            
    CHAR __ss_pad2[(128 - (sizeof(USHORT) + ((sizeof(__int64)) - sizeof(USHORT)) + (sizeof(__int64))))];  
                                   
                                   
                                   
} SOCKADDR_STORAGE_LH, *PSOCKADDR_STORAGE_LH,  *LPSOCKADDR_STORAGE_LH;

#pragma region Desktop Family

typedef struct sockaddr_storage_xp {
    short ss_family;               

    CHAR __ss_pad1[((sizeof(__int64)) - sizeof(USHORT))];  
                                   
                                   
                                   
    __int64 __ss_align;            
    CHAR __ss_pad2[(128 - (sizeof(USHORT) + ((sizeof(__int64)) - sizeof(USHORT)) + (sizeof(__int64))))];  
                                   
                                   
                                   
} SOCKADDR_STORAGE_XP, *PSOCKADDR_STORAGE_XP,  *LPSOCKADDR_STORAGE_XP;
#line 354 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#pragma endregion


typedef SOCKADDR_STORAGE_LH SOCKADDR_STORAGE;
typedef SOCKADDR_STORAGE *PSOCKADDR_STORAGE,  *LPSOCKADDR_STORAGE;



#line 363 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"


typedef struct _SOCKET_PROCESSOR_AFFINITY {
    PROCESSOR_NUMBER Processor;
    USHORT NumaNodeId;
    USHORT Reserved;
} SOCKET_PROCESSOR_AFFINITY, *PSOCKET_PROCESSOR_AFFINITY;
#line 371 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
















#line 388 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"




























#line 417 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"




#line 422 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
















typedef enum {

    IPPROTO_HOPOPTS       = 0,  
#line 442 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
    IPPROTO_ICMP          = 1,
    IPPROTO_IGMP          = 2,
    IPPROTO_GGP           = 3,

    IPPROTO_IPV4          = 4,
#line 448 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

    IPPROTO_ST            = 5,
#line 451 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
    IPPROTO_TCP           = 6,

    IPPROTO_CBT           = 7,
    IPPROTO_EGP           = 8,
    IPPROTO_IGP           = 9,
#line 457 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
    IPPROTO_PUP           = 12,
    IPPROTO_UDP           = 17,
    IPPROTO_IDP           = 22,

    IPPROTO_RDP           = 27,
#line 463 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"


    IPPROTO_IPV6          = 41, 
    IPPROTO_ROUTING       = 43, 
    IPPROTO_FRAGMENT      = 44, 
    IPPROTO_ESP           = 50, 
    IPPROTO_AH            = 51, 
    IPPROTO_ICMPV6        = 58, 
    IPPROTO_NONE          = 59, 
    IPPROTO_DSTOPTS       = 60, 
#line 474 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

    IPPROTO_ND            = 77,

    IPPROTO_ICLFXBM       = 78,
#line 479 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

    IPPROTO_PIM           = 103,
    IPPROTO_PGM           = 113,
    IPPROTO_L2TP          = 115,
    IPPROTO_SCTP          = 132,
#line 485 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
    IPPROTO_RAW           = 255,

    IPPROTO_MAX           = 256,



    IPPROTO_RESERVED_RAW  = 257,
    IPPROTO_RESERVED_IPSEC  = 258,
    IPPROTO_RESERVED_IPSECOFFLOAD  = 259,
    IPPROTO_RESERVED_WNV = 260,
    IPPROTO_RESERVED_MAX  = 261
} IPPROTO, *PIPROTO;


























































                                        











#line 568 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"





































#pragma region Desktop Family or OneCore Family or Games Family





typedef enum {
    ScopeLevelInterface    = 1,
    ScopeLevelLink         = 2,
    ScopeLevelSubnet       = 3,
    ScopeLevelAdmin        = 4,
    ScopeLevelSite         = 5,
    ScopeLevelOrganization = 8,
    ScopeLevelGlobal       = 14,
    ScopeLevelCount        = 16
} SCOPE_LEVEL;

typedef struct {
    union {
        struct {
            ULONG Zone : 28;
            ULONG Level : 4;
        } ;
        ULONG Value;
    } ;
} SCOPE_ID, *PSCOPE_ID;







typedef struct sockaddr_in {



#line 643 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
    ADDRESS_FAMILY sin_family;
#line 645 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

    USHORT sin_port;
    IN_ADDR sin_addr;
    CHAR sin_zero[8];
} SOCKADDR_IN, *PSOCKADDR_IN;

#line 652 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#pragma endregion










typedef struct sockaddr_dl {
    ADDRESS_FAMILY sdl_family;
    UCHAR sdl_data[8];
    UCHAR sdl_zero[4];
} SOCKADDR_DL, *PSOCKADDR_DL;

#line 670 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"






                                        












typedef struct _WSABUF {
    ULONG len;     
      CHAR  *buf; 
} WSABUF,  * LPWSABUF;





typedef struct _WSAMSG {
      LPSOCKADDR       name;              
    INT              namelen;           
    LPWSABUF         lpBuffers;         


    ULONG            dwBufferCount;     


#line 708 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

    WSABUF           Control;           


    ULONG            dwFlags;           


#line 716 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

} WSAMSG, *PWSAMSG, *  LPWSAMSG;






#line 725 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"

typedef struct cmsghdr {
    SIZE_T      cmsg_len;
    INT         cmsg_level;
    INT         cmsg_type;
    
} WSACMSGHDR, *PWSACMSGHDR,  *LPWSACMSGHDR;


typedef WSACMSGHDR CMSGHDR, *PCMSGHDR;
#line 736 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
















#line 753 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"




















#line 774 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"



























#line 802 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"




































#line 839 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
















#line 856 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"






















































typedef struct addrinfo
{
    int                 ai_flags;       
    int                 ai_family;      
    int                 ai_socktype;    
    int                 ai_protocol;    
    size_t              ai_addrlen;     
    char *              ai_canonname;   
      struct sockaddr *   ai_addr;        
    struct addrinfo *   ai_next;        
}
ADDRINFOA, *PADDRINFOA;

typedef struct addrinfoW
{
    int                 ai_flags;       
    int                 ai_family;      
    int                 ai_socktype;    
    int                 ai_protocol;    
    size_t              ai_addrlen;     
    PWSTR               ai_canonname;   
      struct sockaddr *   ai_addr;        
    struct addrinfoW *  ai_next;        
}
ADDRINFOW, *PADDRINFOW;



#pragma region App Family or OneCore Family or Games Family

typedef struct __declspec(deprecated("Use " "ADDRINFOEXW" " instead or define _WINSOCK_DEPRECATED_NO_WARNINGS to disable deprecated API warnings")) addrinfoexA
{
    int                 ai_flags;       
    int                 ai_family;      
    int                 ai_socktype;    
    int                 ai_protocol;    
    size_t              ai_addrlen;     
    char               *ai_canonname;   
    struct sockaddr    *ai_addr;        
    void               *ai_blob;
    size_t              ai_bloblen;
    LPGUID              ai_provider;
    struct addrinfoexA *ai_next;        
} ADDRINFOEXA, *PADDRINFOEXA, *LPADDRINFOEXA;
#line 955 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#pragma endregion

typedef struct addrinfoexW
{
    int                 ai_flags;       
    int                 ai_family;      
    int                 ai_socktype;    
    int                 ai_protocol;    
    size_t              ai_addrlen;     
    PWSTR               ai_canonname;   
      struct sockaddr    *ai_addr;        
      void               *ai_blob;
    size_t              ai_bloblen;
    LPGUID              ai_provider;
    struct addrinfoexW *ai_next;        
} ADDRINFOEXW, *PADDRINFOEXW, *LPADDRINFOEXW;

#line 973 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"









#pragma region Desktop Family

typedef struct __declspec(deprecated("Use " "ADDRINFOEX2W" " instead or define _WINSOCK_DEPRECATED_NO_WARNINGS to disable deprecated API warnings")) addrinfoex2A
{
    int                  ai_flags;       
    int                  ai_family;      
    int                  ai_socktype;    
    int                  ai_protocol;    
    size_t               ai_addrlen;     
    char                *ai_canonname;   
    struct sockaddr     *ai_addr;        
    void                *ai_blob;
    size_t              ai_bloblen;
    LPGUID               ai_provider;
    struct addrinfoex2A *ai_next;        
    int                  ai_version;
    char                *ai_fqdn;
} ADDRINFOEX2A, *PADDRINFOEX2A, *LPADDRINFOEX2A;
#line 1001 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#pragma endregion

typedef struct addrinfoex2W
{
    int                  ai_flags;       
    int                  ai_family;      
    int                  ai_socktype;    
    int                  ai_protocol;    
    size_t               ai_addrlen;     
    PWSTR                ai_canonname;   
      struct sockaddr    *ai_addr;        
      void               *ai_blob;
    size_t               ai_bloblen;
    LPGUID               ai_provider;
    struct addrinfoex2W *ai_next;        
    int                  ai_version;
    PWSTR                ai_fqdn;
} ADDRINFOEX2W, *PADDRINFOEX2W, *LPADDRINFOEX2W;

typedef struct addrinfoex3
{
    int                  ai_flags;       
    int                  ai_family;      
    int                  ai_socktype;    
    int                  ai_protocol;    
    size_t               ai_addrlen;     
    PWSTR                ai_canonname;   
      struct sockaddr    *ai_addr;        
      void               *ai_blob;
    size_t               ai_bloblen;
    LPGUID                 ai_provider;
    struct addrinfoex3   *ai_next;        
    int                  ai_version;
    PWSTR                ai_fqdn;
    int                  ai_interfaceindex;
} ADDRINFOEX3, *PADDRINFOEX3, *LPADDRINFOEX3;

typedef struct addrinfoex4
{
    int                  ai_flags;       
    int                  ai_family;      
    int                  ai_socktype;    
    int                  ai_protocol;    
    size_t               ai_addrlen;     
    PWSTR                ai_canonname;   
      struct sockaddr    *ai_addr;        
      void               *ai_blob;
    size_t               ai_bloblen;
    GUID                 *ai_provider;
    struct addrinfoex4   *ai_next;        
    int                  ai_version;
    PWSTR                ai_fqdn;
    int                  ai_interfaceindex;
    HANDLE               ai_resolutionhandle;
} ADDRINFOEX4, *PADDRINFOEX4, *LPADDRINFOEX4;

typedef struct addrinfoex5
{
    int                  ai_flags;       
    int                  ai_family;      
    int                  ai_socktype;    
    int                  ai_protocol;    
    size_t               ai_addrlen;     
    PWSTR                ai_canonname;   
      struct sockaddr    *ai_addr;        
      void               *ai_blob;
    size_t               ai_bloblen;
    GUID                 *ai_provider;
    struct addrinfoex5   *ai_next;        
    int                  ai_version;
    PWSTR                ai_fqdn;
    int                  ai_interfaceindex;
    HANDLE               ai_resolutionhandle;
    unsigned int         ai_ttl;          
} ADDRINFOEX5, *PADDRINFOEX5, *LPADDRINFOEX5;


















typedef struct addrinfo_dns_server
{
    unsigned int     ai_servertype;
    unsigned __int64 ai_flags;
    unsigned int     ai_addrlen;
      struct sockaddr *ai_addr;

    union
    {
        PWSTR ai_template;
    };
} ADDRINFO_DNS_SERVER;









typedef struct addrinfoex6
{
    int                  ai_flags;       
    int                  ai_family;      
    int                  ai_socktype;    
    int                  ai_protocol;    
    size_t               ai_addrlen;     
    PWSTR                ai_canonname;   
      struct sockaddr    *ai_addr;        
      void               *ai_blob;
    size_t               ai_bloblen;
    GUID                 *ai_provider;
    struct addrinfoex5   *ai_next;        
    int                  ai_version;
    PWSTR                ai_fqdn;
    int                  ai_interfaceindex;
    HANDLE               ai_resolutionhandle;
    unsigned int         ai_ttl;          
    unsigned int         ai_numservers;
    ADDRINFO_DNS_SERVER  *ai_servers;
    ULONG64              ai_responseflags;
} ADDRINFOEX6, *PADDRINFOEX6;

#line 1139 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"























#line 1163 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"



#line 1167 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"











#line 1179 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"






















#pragma warning(pop)


}
#line 1206 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#line 1207 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\shared\\ws2def.h"
#line 117 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"





typedef UINT_PTR        SOCKET;











#line 135 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"

typedef struct fd_set {
        u_int fd_count;               
        SOCKET  fd_array[64];   
} fd_set;

extern int __stdcall  __WSAFDIsSet(SOCKET fd, fd_set  *);






































struct timeval {
        long    tv_sec;         
        long    tv_usec;        
};



























                                        
























struct  hostent {
        char     * h_name;           
        char     *  * h_aliases;  
        short   h_addrtype;             
        short   h_length;               
        char     *  * h_addr_list; 

};





struct  netent {
        char     * n_name;           
        char     *  * n_aliases;  
        short   n_addrtype;             
        u_long  n_net;                  
};

struct  servent {
        char     * s_name;           
        char     *  * s_aliases;  

        char     * s_proto;          
        short   s_port;                 



#line 266 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"
};

struct  protoent {
        char     * p_name;           
        char     *  * p_aliases;  
        short   p_proto;                
};














































                                        












































typedef struct WSAData {
        WORD                    wVersion;
        WORD                    wHighVersion;

        unsigned short          iMaxSockets;
        unsigned short          iMaxUdpDg;
        char  *              lpVendorInfo;
        char                    szDescription[256+1];
        char                    szSystemStatus[128+1];






#line 380 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"
} WSADATA,  * LPWSADATA;






































































#line 452 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"


                                       
                                       
                                       





struct sockproto {
        u_short sp_family;              
        u_short sp_protocol;            
};































#line 498 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"






struct  linger {
        u_short l_onoff;                
        u_short l_linger;               
};


















#line 527 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"



#line 531 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"




























































































































































































































































































#line 816 "C:\\Program Files (x86)\\Windows Kits\\10\\Include\\10.0.22621.0\\um\\winsock2.h"











typedef struct _OVERLAPPED *    LPWSAOVERLAPPED;






