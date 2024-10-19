echo -e " Installing BitCoin PPA...$COL_RESET"
if [ ! -f /etc/apt/sources.list.d/bitcoin.list ]; then
sudo add-apt-repository -y ppa:bitcoin/bitcoin
fi

mkdir -p kriptokyng_setup/tmp
cd kriptokyng_setup/tmp

echo -e "Temp Folder Created$COL_RESET"

##################################################################################### Build Berkeley 4.8 ####################################################################
sudo apt-get install software-properties-common
sudo add-apt-repository ppa:bitcoin/bitcoin
sudo apt-get update
sudo apt-get install libdb4.8-dev libdb4.8++-dev
sudo apt-get install build-essential libtool autotools-dev automake pkg-config libssl-dev libevent-dev bsdmainutils libboost-all-dev libcurl4-openssl-dev

#############################################################################################################################################################################

echo -e " Installing additional system files required for daemons...$COL_RESET"
sudo apt-get update
sudo apt-get install build-essential libtool autotools-dev automake pkg-config libssl-dev libevent-dev bsdmainutils

sudo apt-get install libboost-system-dev libboost-filesystem-dev libboost-chrono-dev libboost-program-options-dev libboost-test-dev libboost-thread-dev

sudo apt-get install software-properties-common build-essential libtool git autotools-dev \
automake pkg-config libssl-dev libevent-dev bsdmainutils git libboost-all-dev libminiupnpc-dev \
libqt5gui5 libqt5core5a libqt5webkit5-dev libqt5dbus5 qttools5-dev qttools5-dev-tools libprotobuf-dev \
protobuf-compiler libqrencode-dev libzmq3-dev libgmp-dev libsqlite3-dev -y

echo -e "Installing PostgreSQL v13.0$COL_RESET"

echo -e " Installing additional system files required for daemons...$COL_RESET"
sudo apt -y install vim bash-completion wget
wget --quiet -O - https://www.postgresql.org/media/keys/ACCC4CF8.asc | sudo apt-key add -
echo "deb http://apt.postgresql.org/pub/repos/apt/ lsb_release -cs-pgdg main" |sudo tee /etc/apt/sources.list.d/pgdg.list
echo "deb http://apt.postgresql.org/pub/repos/apt/ lsb_release -cs-pgdg-testing main 13" |sudo tee /etc/apt/sources.list.d/pgdg-testing.list
sudo apt-get update
sudo apt install postgresql postgresql-contrib -y

echo -e "Installed PostgreSQL v13.0 $COL_RESET"

echo -e " Building Berkeley 4.8, this may take several minutes...$COL_RESET"
mkdir -p /home/kriptokyng/berkeley/db4/
cd /home/kriptokyng/miningcore_setup/tmp
wget 'http://download.oracle.com/berkeley-db/db-4.8.30.NC.tar.gz'
tar -xzvf db-4.8.30.NC.tar.gz
cd db-4.8.30.NC/build_unix/
../dist/configure --enable-cxx --disable-shared --with-pic --prefix=/home/kriptokyng/berkeley/db4/
make -j6
make install
cd /home/kriptokyng/miningcore_setup/tmp
sudo rm -r db-4.8.30.NC.tar.gz db-4.8.30.NC

echo -e "Berkeley 4.8 Completed...$COL_RESET"

echo -e " Building Berkeley 5.1, this may take several minutes...$COL_RESET"
mkdir -p /home/kriptokyng/berkeley/db5/
cd /home/kriptokyng/miningcore_setup/tmp
wget 'http://download.oracle.com/berkeley-db/db-5.1.29.tar.gz
tar -xzvf db-5.1.29.tar.gz
cd db-5.1.29/build_unix/
../dist/configure --enable-cxx --disable-shared --with-pic --prefix=/home/kriptokyng/berkeley/db5/
make -j6
make install
cd /home/kriptokyng/kriptokyng_setup/tmp
sudo rm -r db-5.1.29.tar.gz db-5.1.29

echo -e "Berkeley 5.1 Completed...$COL_RESET"

echo -e " Building Berkeley 5.3, this may take several minutes...$COL_RESET"
mkdir -p /home/kriptokyng/berkeley/db5.3/
cd /home/kriptokyng/miningcore_setup/tmp
wget 'http://download.oracle.com/berkeley-db/db-5.3.28.tar.gz'
tar -xzvf db-5.3.28.tar.gz
cd db-5.3.28/build_unix/
../dist/configure --enable-cxx --disable-shared --with-pic --prefix=/home/kriptokyng/berkeley/db5.3/
make -j6
make install
cd /home/kriptokyng/miningcore_setup/tmp
sudo rm -r db-5.3.28.tar.gz db-5.3.28

echo -e "Berkeley 5.3 Completed...$COL_RESET"

echo -e " Building MiningCore, this may take several minutes...$COL_RESET"
wget -q https://packages.microsoft.com/config/ubuntu/16.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update -y
sudo apt-get install apt-transport-https -y
sudo apt-get update -y
sudo apt-get -y install dotnet-sdk-2.2 git cmake build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5
cd
git clone https://github.com/coinfoundry/miningcore
cd miningcore/src/Miningcore

echo -e "MiningCore Build Completed...$COL_RESET"

