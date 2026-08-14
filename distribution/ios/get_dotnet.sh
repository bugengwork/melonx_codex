#!/bin/bash

# XCCONFIG_FILE="${SRCROOT}/MeloNX.xcconfig"

SEARCH_PATHS=(
    "/usr/local/share/dotnet"
    "/usr/local/bin"
    "/usr/bin"
    "/bin"
    "/opt"
    "/Library/Frameworks"
    "$HOME/.dotnet"
    "$HOME/dotnet"
    "$HOME/Developer"
    "~/.dotnet"
    "~/dotnet"
    "~/Developer"
)



# Allow CI and local validation to select an isolated SDK explicitly. The
# existing search order remains unchanged when no compatible override is supplied.
if [ -n "$MELONX_DOTNET" ] && [ -x "$MELONX_DOTNET" ] && "$MELONX_DOTNET" --version >/dev/null 2>&1; then
    echo "$MELONX_DOTNET"
    exit 0
fi

for path in "${SEARCH_PATHS[@]}"; do
    if [ -d "$path" ]; then
        DOTNET_PATH=$(find "$path" -name dotnet -type f -print -quit 2>/dev/null)
        if [ -n "$DOTNET_PATH" ] && [ -x "$DOTNET_PATH" ] && "$DOTNET_PATH" --version >/dev/null 2>&1; then
            echo "$DOTNET_PATH"
            exit 0
        fi
    fi
done

echo "No installed .NET SDK satisfies global.json." >&2
exit 1
