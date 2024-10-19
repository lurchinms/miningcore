#!/bin/bash
mkdir -p miningcore_setup/tmp
cd miningcore_setup/tmp

echo -e "Temp Folder Created$COL_RESET"

echo -e " Building Berkeley 4.8, this may take several minutes...$COL_RESET"
mkdir -p /home/kriptokyng/berkeley/db4/
cd /home/kriptokyng/miningcore_setup/tmp
wget 'http://download.oracle.com/berkeley-db/db-4.8.30.NC.tar.gz'
tar -xzvf db-4.8.30.NC.tar.gz
cd db-4.8.30.NC/build_unix/
../dist/configure --enable-cxx --disable-shared --with-pic --prefix=/home/kriptokyng/berkeley/db4/
make install
cd /home/kriptokyng/miningcore_setup/tmp
sudo rm -r db-4.8.30.NC.tar.gz db-4.8.30.NC

echo -e "Berkeley 4.8 Completed...$COL_RESET"

echo -e " Building Berkeley 5.1, this may take several minutes...$COL_RESET"
mkdir -p /home/kriptokyng/berkeley/db5/
cd /home/kriptokyng/miningcore_setup/tmp
wget 'http://download.oracle.com/berkeley-db/db-5.1.29.tar.gz'
tar -xzvf db-5.1.29.tar.gz
cd db-5.1.29/build_unix/
../dist/configure --enable-cxx --disable-shared --with-pic --prefix=/home/kriptokyng/berkeley/db5/
make install
cd /home/kriptokyng/kriptokyng_setup/tmp
sudo rm -r db-5.1.29.tar.gz db-5.1.29

echo -e "Berkeley 5.1 Completed...$COL_RESET"

echo -e " Building Berkeley 5.3, this may take several minutes...$COL_RESET"
mkdir -p /home/kriptokyng/berkeley/db5.3/
cd /home/kriptokyng/miningcore_setup/tmp
wget 'http://anduin.linuxfromscratch.org/BLFS/bdb/db-5.3.28.tar.gz'
tar -xzvf db-5.3.28.tar.gz
cd db-5.3.28/build_unix/
../dist/configure --enable-cxx --disable-shared --with-pic --prefix=/home/kriptokyng/berkeley/db5.3/
make install
cd /home/kriptokyng/miningcore_setup/tmp
sudo rm -r db-5.3.28.tar.gz db-5.3.28

echo -e "Berkeley 5.3 Completed...$COL_RESET"
