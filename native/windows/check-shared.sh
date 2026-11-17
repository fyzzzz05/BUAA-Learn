#!/usr/bin/env sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$project_dir/../.." && pwd)
if ! command -v cargo >/dev/null 2>&1; then
    if [ -x "$HOME/.cargo/bin/cargo" ]; then
        PATH="$HOME/.cargo/bin:$PATH"
    else
        case "$(uname -m)" in arm64) rust_arch=aarch64 ;; x86_64) rust_arch=x86_64 ;; *) rust_arch=unknown ;; esac
        rust_bin="$HOME/.rustup/toolchains/stable-${rust_arch}-apple-darwin/bin"
        if [ ! -x "$rust_bin/cargo" ]; then printf '%s\n' 'Install Rust or add cargo to PATH.' >&2; exit 1; fi
        PATH="$rust_bin:$PATH"
    fi
    export PATH
fi
if command -v dotnet >/dev/null 2>&1; then dotnet_bin=$(command -v dotnet)
elif [ -x "$root/dist/native/windows/tools/dotnet/dotnet" ]; then dotnet_bin="$root/dist/native/windows/tools/dotnet/dotnet"
else printf '%s\n' 'Install the .NET SDK pinned by native/windows/global.json.' >&2; exit 1
fi
if [ "$(uname -s)" = Darwin ]; then
    MACOSX_DEPLOYMENT_TARGET=${MACOSX_DEPLOYMENT_TARGET:-13.0}
    DYLD_LIBRARY_PATH="$root/native/core/target/release${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
    export MACOSX_DEPLOYMENT_TARGET
    export DYLD_LIBRARY_PATH
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$project_dir"
cargo test --locked --manifest-path "$root/native/core/Cargo.toml"
cargo build --release --locked --manifest-path "$root/native/core/Cargo.toml"
"$dotnet_bin" restore BUAALearn.Tests/BUAALearn.Tests.csproj --locked-mode
"$dotnet_bin" run --project BUAALearn.Tests -c Release --no-restore
