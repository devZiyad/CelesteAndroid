#!/usr/bin/env bash
# Builds SDL3, FNA3D and FAudio for Android with the NDK and copies the .so files to natives/android-<abi>.
# Usage: scripts/build-natives-android.sh [--abi arm64-v8a] [--clean]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"

ABI=arm64-v8a; API=26; CLEAN=0
while [ $# -gt 0 ]; do
	case "$1" in
		--abi) ABI="$2"; shift 2 ;;
		--clean) CLEAN=1; shift ;;
		*) die "unknown argument: $1" ;;
	esac
done

NDK="$ANDROID_NDK_HOME"
[ -f "$NDK/build/cmake/android.toolchain.cmake" ] || die "NDK not found (ANDROID_NDK_HOME=$NDK)"
case "$ABI" in
	arm64-v8a) ABI_DIR=android-arm64 ;;
	x86_64) ABI_DIR=android-x64 ;;
	*) die "unsupported ABI: $ABI" ;;
esac
BUILD="$ROOT/build/$ABI_DIR"
OUT="$ROOT/natives/$ABI_DIR"
[ "$CLEAN" = 1 ] && rm -rf "$BUILD"
mkdir -p "$BUILD" "$OUT"

GEN=()
command -v ninja >/dev/null && GEN=(-G Ninja)
COMMON=("${GEN[@]}"
	"-DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake"
	"-DANDROID_ABI=$ABI" "-DANDROID_PLATFORM=android-$API"
	# 16 KB pages (Android 15+); harmless on 4 KB devices.
	-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON
	-DCMAKE_BUILD_TYPE=Release
	# CMake 4.x rejects cmake_minimum_required < 3.5 in some submodules.
	-DCMAKE_POLICY_VERSION_MINIMUM=3.5)

cmake_build() { # name src extra-args...
	local name="$1" src="$2"; shift 2
	echo "=== $name ===" >&2
	cmake -S "$src" -B "$BUILD/$name" "${COMMON[@]}" "$@" >&2
	cmake --build "$BUILD/$name" --parallel >&2
}

cmake_build SDL3 "$ROOT/external/SDL" -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_TEST_LIBRARY=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF
SDL3=("-DSDL3_INCLUDE_DIRS=$ROOT/external/SDL/include" "-DSDL3_LIBRARIES=$BUILD/SDL3/libSDL3.so")
cmake_build FNA3D "$ROOT/external/FNA/lib/FNA3D" -DBUILD_SDL3=ON "${SDL3[@]}"
cmake_build FAudio "$ROOT/external/FNA/lib/FAudio" -DBUILD_SDL3=ON "${SDL3[@]}"

STRIP="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip"
for so in "$BUILD/SDL3/libSDL3.so" "$BUILD/FNA3D/libFNA3D.so" "$BUILD/FAudio/libFAudio.so"; do
	cp -f "$so" "$OUT/"
	"$STRIP" --strip-unneeded "$OUT/$(basename "$so")"
done
ls -l "$OUT"/*.so
