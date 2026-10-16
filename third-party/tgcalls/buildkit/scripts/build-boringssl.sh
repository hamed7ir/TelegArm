#!/usr/bin/env bash
# BoringSSL a0cb538b (hamed7ir/adb-rt's ARM32-proven pin), static crypto+ssl, OPENSSL_NO_ASM (its ARM32 .S files are
# gated on __ELF__ and come out empty on COFF). Patch 0001 applied in the source tree.
# x86: BSSL_ARGS="-DCMAKE_ASM_NASM_COMPILER=<nasm>" (NASM asm on).
. /d/tgcalls/scripts/env.sh
set -e
TC=${TC:-$TC_ARM}; PFX=${PFX:-$PREFIX_ARM}; BD=${BD:-$B/boringssl-arm32}
"$CMAKE" -G Ninja -S D:/tgcalls/deps/boringssl -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
  -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release ${BSSL_ARGS--DOPENSSL_NO_ASM=1} -DBUILD_TESTING=OFF
"$CMAKE" --build $BD --target crypto ssl -- -j $JOBS
