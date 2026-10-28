#!/usr/bin/env bash
# Builds vodozemac-ffi, the native oracle that the managed encryption is tested against, and copies the
# libraries to tests/Uniform.Matrix.Protocol.Crypto.Tests/runtimes/<rid>/native/. The client itself needs no native code.
# Only the host target is needed for tests; other targets are built when their cross-linker exists.
#
# Toolchain (see docs/articles/building-and-testing.md):
#   rustup in ~/.cargo (pinned by native/vodozemac-ffi/rust-toolchain.toml)
#   linux-arm64: sudo apt install gcc-aarch64-linux-gnu
#   win-x64:     sudo apt install gcc-mingw-w64-x86-64
#   win-arm64:   llvm-mingw; set LLVM_MINGW to its directory (default ~/.local/llvm-mingw)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
crate="$root/native/vodozemac-ffi"
runtimes="$root/tests/Uniform.Matrix.Protocol.Crypto.Tests/runtimes"
cargo="${CARGO:-$HOME/.cargo/bin/cargo}"
llvm_mingw="${LLVM_MINGW:-$HOME/.local/llvm-mingw}"

# rid | rust target | linker | library file
targets=(
  "linux-x64|x86_64-unknown-linux-gnu||libvodozemac_ffi.so"
  "linux-arm64|aarch64-unknown-linux-gnu|aarch64-linux-gnu-gcc|libvodozemac_ffi.so"
  "win-x64|x86_64-pc-windows-gnu|x86_64-w64-mingw32-gcc|vodozemac_ffi.dll"
  "win-arm64|aarch64-pc-windows-gnullvm|$llvm_mingw/bin/aarch64-w64-mingw32-clang|vodozemac_ffi.dll"
)

built=0
skipped=()
for entry in "${targets[@]}"; do
  IFS='|' read -r rid target linker library <<< "$entry"

  # Skip when the cross-linker is not installed
  if [[ -n "$linker" ]] && ! command -v "$linker" > /dev/null 2>&1; then
    skipped+=("$rid (missing $linker)")
    continue
  fi

  # Build with the target's linker
  echo "== $rid ($target)"
  env_name="CARGO_TARGET_$(echo "$target" | tr 'a-z-' 'A-Z_')_LINKER"
  if [[ -n "$linker" ]]; then export "$env_name=$linker"; fi
  (cd "$crate" && "$cargo" build --release --target "$target")

  # Copy into the .NET runtimes folder
  mkdir -p "$runtimes/$rid/native"
  cp "$crate/target/$target/release/$library" "$runtimes/$rid/native/$library"
  built=$((built + 1))
done

echo
echo "Built $built of ${#targets[@]} targets into $runtimes"
for s in "${skipped[@]}"; do echo "  skipped $s"; done
