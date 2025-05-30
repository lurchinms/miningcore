using System.Globalization;
using System.Numerics;
using Autofac;
using JetBrains.Annotations;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Crypto.Hashing.Ethash;
using Miningcore.Crypto.Hashing.Progpow;
using NBitcoin;
using Newtonsoft.Json;

namespace Miningcore.Configuration;

public abstract partial class CoinTemplate
{
    public T As<T>() where T : CoinTemplate
    {
        return (T) this;
    }

    public abstract string GetAlgorithmName();

    /// <summary>
    /// json source file where this template originated from
    /// </summary>
    [JsonIgnore]
    public string Source { get; set; }
}

public partial class AlephiumCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        return "Blake3";
    }

    #endregion
}

public partial class BeamCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        return "BeamHash";
    }

    #endregion
}

public partial class BitcoinTemplate
{
    public BitcoinTemplate()
    {
        merkleTreeHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, MerkleTreeHasher));

        coinbaseHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, CoinbaseHasher));

        headerHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, HeaderHasher));

        shareHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, ShareHasher));

        blockHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, BlockHasher));

        posBlockHasherValue = new Lazy<IHashAlgorithm>(() =>
            HashAlgorithmFactory.GetHash(ComponentContext, PoSBlockHasher));
    }

    private readonly Lazy<IHashAlgorithm> merkleTreeHasherValue;
    private readonly Lazy<IHashAlgorithm> coinbaseHasherValue;
    private readonly Lazy<IHashAlgorithm> headerHasherValue;
    private readonly Lazy<IHashAlgorithm> shareHasherValue;
    private readonly Lazy<IHashAlgorithm> blockHasherValue;
    private readonly Lazy<IHashAlgorithm> posBlockHasherValue;

    public IComponentContext ComponentContext { get; [UsedImplicitly] init; }

    public IHashAlgorithm MerkleTreeHasherValue => merkleTreeHasherValue.Value;
    public IHashAlgorithm CoinbaseHasherValue => coinbaseHasherValue.Value;
    public IHashAlgorithm HeaderHasherValue => headerHasherValue.Value;
    public IHashAlgorithm ShareHasherValue => shareHasherValue.Value;
    public IHashAlgorithm BlockHasherValue => blockHasherValue.Value;
    public IHashAlgorithm PoSBlockHasherValue => posBlockHasherValue.Value;

    public BitcoinNetworkParams GetNetwork(ChainName chain)
    {
        if(Networks == null || Networks.Count == 0)
            return null;

        if(chain == ChainName.Mainnet)
            return Networks["main"];
        else if(chain == ChainName.Testnet)
            return Networks["test"];
        else if(chain == ChainName.Regtest)
            return Networks["regtest"];

        throw new NotSupportedException("unsupported network type");
    }

    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        switch(Symbol)
        {
            case "HNS":
                return HeaderHasherValue.GetType().Name + " + " + ShareHasherValue.GetType().Name;
            case "KCN":
                return HeaderHasherValue.GetType().Name;
            default:
                var hash = HeaderHasherValue;

                if(hash.GetType() == typeof(DigestReverser))
                    return ((DigestReverser) hash).Upstream.GetType().Name;

                return hash.GetType().Name;
        }
    }

    #endregion
}

public partial class EquihashCoinTemplate
{
    public partial class EquihashNetworkParams
    {
        public EquihashNetworkParams()
        {
            diff1Value = new Lazy<Org.BouncyCastle.Math.BigInteger>(() =>
            {
                if(string.IsNullOrEmpty(Diff1))
                    throw new InvalidOperationException("Diff1 has not yet been initialized");

                return new Org.BouncyCastle.Math.BigInteger(Diff1, 16);
            });

            diff1BValue = new Lazy<BigInteger>(() =>
            {
                if(string.IsNullOrEmpty(Diff1))
                    throw new InvalidOperationException("Diff1 has not yet been initialized");

                return BigInteger.Parse(Diff1, NumberStyles.HexNumber);
            });
        }

        private readonly Lazy<Org.BouncyCastle.Math.BigInteger> diff1Value;
        private readonly Lazy<BigInteger> diff1BValue;

        [JsonIgnore]
        public Org.BouncyCastle.Math.BigInteger Diff1Value => diff1Value.Value;

        [JsonIgnore]
        public BigInteger Diff1BValue => diff1BValue.Value;

        [JsonIgnore]
        public ulong FoundersRewardSubsidySlowStartShift => FoundersRewardSubsidySlowStartInterval / 2;

        [JsonIgnore]
        public ulong LastFoundersRewardBlockHeight => FoundersRewardSubsidyHalvingInterval + FoundersRewardSubsidySlowStartShift - 1;
    }

    public EquihashNetworkParams GetNetwork(ChainName chain)
    {
        if(chain == ChainName.Mainnet)
            return Networks["main"];
        else if(chain == ChainName.Testnet)
            return Networks["test"];
        else if(chain == ChainName.Regtest)
            return Networks["regtest"];

        throw new NotSupportedException("unsupported network type");
    }

    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        switch(Symbol)
        {
            case "VRSC":
                return "Verushash";
            default:
                // TODO: return variant
                return "Equihash";
        }
    }

    #endregion
}

public partial class ConcealCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
//        switch(Hash)
//        {
//            case CryptonightHashType.RandomX:
//                return "RandomX";
//        }

        return Hash.ToString();
    }

    #endregion
}

public partial class CryptonoteCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
//        switch(Hash)
//        {
//            case CryptonightHashType.RandomX:
//                return "RandomX";
//        }

        return Hash.ToString();
    }

    #endregion
}

public partial class ErgoCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        return "Autolykos";
    }

    #endregion
}

public partial class EthereumCoinTemplate
{
    #region Overrides of CoinTemplate
    
    public EthereumCoinTemplate()
    {
        ethashLightValue = new Lazy<IEthashLight>(() =>
            EthashFactory.GetEthash(Symbol, ComponentContext, Ethasher));
    }

    private readonly Lazy<IEthashLight> ethashLightValue;

    public IComponentContext ComponentContext { get; [UsedImplicitly] init; }

    public IEthashLight Ethash => ethashLightValue.Value;

    public override string GetAlgorithmName()
    {
        return Ethash.AlgoName;
    }

    #endregion
}

public partial class KaspaCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        switch(Symbol)
        {
            case "AIX":
                return "AstrixHash";
            case "KLS":
                return "Karlsenhashv2";
            case "CSS":
            case "NTL":
            case "NXL":
            case "PUG":
                return "Karlsenhash";
            case "CAS":
            case "HTN":
            case "PYI":
                return "Pyrinhash";
            case "SPR":
                return " SpectreX";
            default:
                // TODO: return variant
                return "kHeavyHash";
        }
    }

    #endregion
}

public partial class ProgpowCoinTemplate
{
    #region Overrides of CoinTemplate
    
    public ProgpowCoinTemplate() : base()
    {
        progpowLightValue = new Lazy<IProgpowLight>(() =>
            ProgpowFactory.GetProgpow(Symbol, ComponentContext, Progpower));
    }

    private readonly Lazy<IProgpowLight> progpowLightValue;

    public IProgpowLight ProgpowHasher => progpowLightValue.Value;

    public override string GetAlgorithmName()
    {
        return ProgpowHasher.AlgoName;
    }

    #endregion
}

public partial class WarthogCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        return "PoBW";
    }

    #endregion
}

public partial class XelisCoinTemplate
{
    #region Overrides of CoinTemplate

    public override string GetAlgorithmName()
    {
        return "XelisHash";
    }

    #endregion
}

public partial class ZanoCoinTemplate
{
    #region Overrides of CoinTemplate

    public ZanoCoinTemplate() : base()
    {
        progpowLightValue = new Lazy<IProgpowLight>(() =>
            ProgpowFactory.GetProgpow(Symbol, ComponentContext, Hash.ToString().ToLower()));
    }

    private readonly Lazy<IProgpowLight> progpowLightValue;

    public IComponentContext ComponentContext { get; [UsedImplicitly] init; }

    public IProgpowLight ProgpowHasher => progpowLightValue.Value;

    public override string GetAlgorithmName()
    {
        return Hash.ToString();
    }

    #endregion
}

public partial class PoolConfig
{
    /// <summary>
    /// Back-reference to coin template for this pool
    /// </summary>
    [JsonIgnore]
    public CoinTemplate Template { get; set; }
}
