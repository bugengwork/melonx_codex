#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_PATH="$REPO_DIR/src/MeloNX/MeloNX.xcodeproj"
SCHEME="MeloNX"
CONFIGURATION="Release"
DERIVED_DATA_PATH="$REPO_DIR/build/DerivedData-automation"
OUTPUT_DIR="$REPO_DIR/build/ipa"
OUTPUT_PATH=""
PLUMESIGN_PATH="${MELONX_PLUMESIGN:-}"
DEVICE_UDID=""
SHOULD_BUILD=1
SHOULD_INSTALL=0
STAGING_DIR=""
CURRENT_STEP=0
TOTAL_STEPS=4
CURRENT_TITLE="准备启动"
FAIL_GUIDANCE="检查上方错误后重新运行脚本。"
BUILD_LOG_PATH=""

if [ -t 1 ] && [ "${TERM:-dumb}" != "dumb" ]; then
    COLOR_BLUE='\033[1;34m'
    COLOR_GREEN='\033[1;32m'
    COLOR_YELLOW='\033[1;33m'
    COLOR_RED='\033[1;31m'
    COLOR_DIM='\033[2m'
    COLOR_RESET='\033[0m'
else
    COLOR_BLUE=''
    COLOR_GREEN=''
    COLOR_YELLOW=''
    COLOR_RED=''
    COLOR_DIM=''
    COLOR_RESET=''
fi

usage() {
    printf '%s\n' \
        "Usage: $0 [options]" \
        "" \
        "Build and validate a development-signed MeloNX Release IPA." \
        "Impactor signing and device installation only run with --install." \
        "" \
        "Options:" \
        "  --install              Re-sign and install with Impactor's plumesign" \
        "  --udid UDID            Install to this device; otherwise prompt" \
        "  --plumesign PATH        Path to the plumesign executable" \
        "  --output PATH           Seed IPA output path" \
        "  --derived-data PATH     Xcode DerivedData path" \
        "  --skip-build            Package an existing build from DerivedData" \
        "  -h, --help              Show this help"
}

fail() {
    printf '\n%b✗ 卡在步骤 %s/%s：%s%b\n' \
        "$COLOR_RED" "$CURRENT_STEP" "$TOTAL_STEPS" "$CURRENT_TITLE" "$COLOR_RESET" >&2
    printf '  原因：%s\n' "$1" >&2
    if [ "$CURRENT_STEP" -eq 2 ] && [ -n "$BUILD_LOG_PATH" ] && [ -f "$BUILD_LOG_PATH" ]; then
        printf '  日志：%s\n' "$BUILD_LOG_PATH" >&2
    fi
    printf '%b  你需要做：%s%b\n' "$COLOR_YELLOW" "$FAIL_GUIDANCE" "$COLOR_RESET" >&2
    exit 1
}

print_plan() {
    printf '\n%bMeloNX 打包与安装向导%b\n' "$COLOR_BLUE" "$COLOR_RESET"
    printf '%s\n' '────────────────────────────────────────'
    printf '  ○ 1. 环境检查               无需操作\n'
    if [ "$SHOULD_BUILD" -eq 1 ]; then
        printf '  ○ 2. Xcode Release 构建      无需操作，耗时最长\n'
    else
        printf '  ○ 2. 读取已有构建            无需操作\n'
    fi
    printf '  ○ 3. 架构与签名权限检查      无需操作\n'
    printf '  ○ 4. IPA 打包与完整性校验    无需操作\n'
    if [ "$SHOULD_INSTALL" -eq 1 ]; then
        printf '  ○ 5. Impactor 安装前检查     可能需要登录 Apple ID\n'
        printf '  ○ 6. 签名并安装到 iPhone     需要连接、解锁并信任电脑\n'
        printf '  ○ 7. StikDebug 开启 JIT      需要在 iPhone 上操作\n'
    fi
    printf '%s\n\n' '────────────────────────────────────────'
}

start_step() {
    CURRENT_STEP="$1"
    CURRENT_TITLE="$2"
    FAIL_GUIDANCE="$4"
    printf '%b[%s/%s] ▶ %s%b\n' \
        "$COLOR_BLUE" "$CURRENT_STEP" "$TOTAL_STEPS" "$CURRENT_TITLE" "$COLOR_RESET"
    printf '  正在执行：%s\n' "$3"
    printf '%b  你需要做：%s%b\n' "$COLOR_YELLOW" "$4" "$COLOR_RESET"
}

complete_step() {
    printf '%b[%s/%s] ✓ %s完成%b\n\n' \
        "$COLOR_GREEN" "$CURRENT_STEP" "$TOTAL_STEPS" "$CURRENT_TITLE" "$COLOR_RESET"
}

run_xcodebuild() {
    xcodebuild \
        -project "$PROJECT_PATH" \
        -scheme "$SCHEME" \
        -configuration "$CONFIGURATION" \
        -sdk iphoneos \
        -destination "generic/platform=iOS" \
        -derivedDataPath "$DERIVED_DATA_PATH" \
        build >"$BUILD_LOG_PATH" 2>&1 &

    local build_pid=$!
    local spinner='|/-\\'
    local index=0
    if [ -t 1 ]; then
        while kill -0 "$build_pid" 2>/dev/null; do
            printf '\r  %b%c 构建中；详细输出已收进日志%b' \
                "$COLOR_BLUE" "${spinner:index%4:1}" "$COLOR_RESET"
            index=$((index + 1))
            sleep 0.2
        done
        printf '\r%*s\r' 58 ''
    fi

    if ! wait "$build_pid"; then
        printf '%b  最近的构建输出：%b\n' "$COLOR_DIM" "$COLOR_RESET" >&2
        tail -n 20 "$BUILD_LOG_PATH" >&2
        fail "Xcode Release 构建失败"
    fi
}

cleanup() {
    if [ -n "$STAGING_DIR" ] && [ -d "$STAGING_DIR" ]; then
        rm -rf "$STAGING_DIR"
    fi
}

trap cleanup EXIT

while [ "$#" -gt 0 ]; do
    case "$1" in
        --install)
            SHOULD_INSTALL=1
            shift
            ;;
        --udid)
            [ "$#" -ge 2 ] || fail "--udid requires a value"
            DEVICE_UDID="$2"
            shift 2
            ;;
        --plumesign)
            [ "$#" -ge 2 ] || fail "--plumesign requires a value"
            PLUMESIGN_PATH="$2"
            shift 2
            ;;
        --output)
            [ "$#" -ge 2 ] || fail "--output requires a value"
            OUTPUT_PATH="$2"
            shift 2
            ;;
        --derived-data)
            [ "$#" -ge 2 ] || fail "--derived-data requires a value"
            DERIVED_DATA_PATH="$2"
            shift 2
            ;;
        --skip-build)
            SHOULD_BUILD=0
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail "unknown option: $1"
            ;;
    esac
done

if [ "$SHOULD_INSTALL" -eq 1 ]; then
    TOTAL_STEPS=7
fi

print_plan

start_step 1 "环境检查" \
    "确认 Xcode、签名、压缩和校验工具可用。" \
    "不需要；如果缺少工具，向导会给出缺失项。"

for command_name in xcodebuild codesign ditto zip unzip shasum file; do
    command -v "$command_name" >/dev/null 2>&1 || {
        FAIL_GUIDANCE="安装或启用 $command_name 后重新运行脚本。"
        fail "缺少命令：$command_name"
    }
done
complete_step

mkdir -p "$OUTPUT_DIR/logs"
BUILD_STAMP="$(date '+%Y%m%d-%H%M%S')"
BUILD_LOG_PATH="$OUTPUT_DIR/logs/MeloNX-$BUILD_STAMP-xcodebuild.log"
APP_PATH="$DERIVED_DATA_PATH/Build/Products/$CONFIGURATION-iphoneos/MeloNX.app"
APP_EXECUTABLE="$APP_PATH/MeloNX"
RYUJINX_DYLIB="$APP_PATH/Frameworks/Ryujinx.Headless.SDL2.dylib"

if [ "$SHOULD_BUILD" -eq 1 ]; then
    start_step 2 "Xcode Release 构建" \
        "编译 MeloNX 和 Ryujinx iOS 依赖；详细输出写入 ${BUILD_LOG_PATH}。" \
        "不需要；等待即可，不要关闭终端或 Xcode。"
    FAIL_GUIDANCE="打开日志查看最后的 Xcode 错误；修复后重新运行。日志：$BUILD_LOG_PATH"
    run_xcodebuild
    complete_step
else
    start_step 2 "读取已有构建" \
        "从 $DERIVED_DATA_PATH 查找上一次 Release 构建。" \
        "不需要；若构建不存在，请去掉 --skip-build 后重试。"
    FAIL_GUIDANCE="去掉 --skip-build 后重新运行，让向导先完成 Xcode Release 构建。"
    [ -d "$APP_PATH" ] || fail "找不到已有 MeloNX.app：$APP_PATH"
    complete_step
fi

start_step 3 "架构与签名权限检查" \
    "检查 MeloNX.app、arm64、开发描述文件、get-task-allow 和 increased-memory-limit。" \
    "不需要；这一步只读取并验证构建产物。"
FAIL_GUIDANCE="去掉 --skip-build 重新构建；若仍失败，查看该步骤报告的具体缺失项。"
[ -d "$APP_PATH" ] || fail "找不到 MeloNX.app：$APP_PATH"
[ -f "$APP_EXECUTABLE" ] || fail "缺少 MeloNX 主程序"
[ -f "$RYUJINX_DYLIB" ] || fail "缺少 Ryujinx.Headless.SDL2.dylib"
[ -f "$APP_PATH/embedded.mobileprovision" ] || fail "缺少 embedded.mobileprovision"
file "$APP_EXECUTABLE" | grep -q 'arm64' || fail "MeloNX 主程序不是 arm64"
file "$RYUJINX_DYLIB" | grep -q 'arm64' || fail "Ryujinx dylib 不是 arm64"

ENTITLEMENTS_OUTPUT="$(codesign -d --entitlements - "$APP_PATH" 2>&1)"
printf '%s\n' "$ENTITLEMENTS_OUTPUT" | grep -q 'get-task-allow' || \
    fail "缺少 get-task-allow；必须使用 Apple Development 签名"
printf '%s\n' "$ENTITLEMENTS_OUTPUT" | grep -q 'com.apple.developer.kernel.increased-memory-limit' || \
    fail "缺少 increased-memory-limit"
complete_step

if [ -z "$OUTPUT_PATH" ]; then
    OUTPUT_PATH="$OUTPUT_DIR/MeloNX-seed-$BUILD_STAMP.ipa"
elif [[ "$OUTPUT_PATH" != /* ]]; then
    OUTPUT_PATH="$REPO_DIR/$OUTPUT_PATH"
fi

mkdir -p "$(dirname "$OUTPUT_PATH")"
start_step 4 "IPA 打包与完整性校验" \
    "生成 IPA、测试压缩包并创建 SHA-256 校验文件。" \
    "不需要；完成后会显示 IPA 的绝对路径。"
FAIL_GUIDANCE="换一个 --output 路径，或保留现有文件后重新运行。"
[ ! -e "$OUTPUT_PATH" ] || fail "为保护已有文件，拒绝覆盖：$OUTPUT_PATH"

STAGING_DIR="$(mktemp -d "$OUTPUT_DIR/.melonx-package.XXXXXX")"
mkdir -p "$STAGING_DIR/Payload"
ditto "$APP_PATH" "$STAGING_DIR/Payload/MeloNX.app"

(
    cd "$STAGING_DIR"
    zip -qry "$OUTPUT_PATH" Payload
)

unzip -tq "$OUTPUT_PATH" >/dev/null
shasum -a 256 "$OUTPUT_PATH" > "$OUTPUT_PATH.sha256"

printf '%s\n' \
    "  IPA：$OUTPUT_PATH" \
    "  校验：$OUTPUT_PATH.sha256"
complete_step

if [ "$SHOULD_INSTALL" -eq 0 ]; then
    printf '%b电脑端打包已全部完成。%b\n' "$COLOR_GREEN" "$COLOR_RESET"
    printf '%b下一步需要你决定：%b如需安装，重新运行并添加 --install。\n' \
        "$COLOR_YELLOW" "$COLOR_RESET"
    exit 0
fi

start_step 5 "Impactor 安装前检查" \
    "查找 Impactor 的 plumesign 命令行程序。" \
    "暂时不需要；若尚未构建或登录，向导会给出命令。"

if [ -z "$PLUMESIGN_PATH" ]; then
    for candidate in \
        "$REPO_DIR/../Impactor/target/release/plumesign" \
        "$REPO_DIR/../Impactor/target/debug/plumesign"; do
        if [ -x "$candidate" ]; then
            PLUMESIGN_PATH="$candidate"
            break
        fi
    done
fi

if [ -z "$PLUMESIGN_PATH" ] && command -v plumesign >/dev/null 2>&1; then
    PLUMESIGN_PATH="$(command -v plumesign)"
fi

FAIL_GUIDANCE="先执行：cd $REPO_DIR/../Impactor && cargo build --release -p plumesign --bin plumesign；然后执行 ./target/release/plumesign account login。"
[ -n "$PLUMESIGN_PATH" ] && [ -x "$PLUMESIGN_PATH" ] || \
    fail "找不到 plumesign；Impactor 还没有构建"

ACCOUNT_LIST_OUTPUT="$("$PLUMESIGN_PATH" account list 2>&1)" || \
    fail "无法读取 Impactor 的 Apple ID 登录状态"
if printf '%s\n' "$ACCOUNT_LIST_OUTPUT" | grep -q 'No accounts found'; then
    if [ -t 0 ]; then
        printf '%b  检测到需要你操作：请按提示输入 Apple ID、密码和双重验证码。%b\n' \
            "$COLOR_YELLOW" "$COLOR_RESET"
        "$PLUMESIGN_PATH" account login || fail "Apple ID 登录没有完成"
    else
        FAIL_GUIDANCE="在交互式终端运行：$PLUMESIGN_PATH account login；登录完成后重新执行本命令。"
        fail "Impactor 尚未登录 Apple ID"
    fi
else
    printf '  Apple ID：已检测到 Impactor 账户配置\n'
fi

SIGNED_OUTPUT="${OUTPUT_PATH%.ipa}-device-signed.ipa"
[ ! -e "$SIGNED_OUTPUT" ] || fail "为保护已有文件，拒绝覆盖：$SIGNED_OUTPUT"
printf '  plumesign：%s\n' "$PLUMESIGN_PATH"
complete_step

start_step 6 "签名并安装到 iPhone" \
    "让 Impactor 注册能力、重新签名并把 MeloNX 安装到设备。" \
    "现在请连接并解锁 iPhone，确认已信任这台 Mac；Apple ID 未登录时先运行：$PLUMESIGN_PATH account login"
FAIL_GUIDANCE="确认 iPhone 已连接、解锁并信任电脑；若提示账户问题，运行：$PLUMESIGN_PATH account login，然后重试。"
if [ -t 0 ]; then
    printf '%b  准备好后按回车开始安装；按 Control-C 可安全取消。%b' \
        "$COLOR_YELLOW" "$COLOR_RESET"
    read -r _
fi
SIGN_ARGS=(
    sign
    --package "$OUTPUT_PATH"
    --apple-id
    --register-and-install
    --output "$SIGNED_OUTPUT"
)

if [ -n "$DEVICE_UDID" ]; then
    SIGN_ARGS+=(--udid "$DEVICE_UDID")
fi

"$PLUMESIGN_PATH" "${SIGN_ARGS[@]}"
unzip -tq "$SIGNED_OUTPUT" >/dev/null
shasum -a 256 "$SIGNED_OUTPUT" > "$SIGNED_OUTPUT.sha256"

printf '%s\n' \
    "  已安装设备签名 IPA：$SIGNED_OUTPUT"
complete_step

start_step 7 "StikDebug 开启 JIT" \
    "电脑端工作已经结束，等待你在 iPhone 上完成运行时 JIT。" \
    "打开 LocalDevVPN 并确认 VPN 已连接；打开 StikDebug，确保已导入当前设备配对文件，再为 MeloNX 点击 Enable JIT。"
printf '%b  此步骤不能由 Mac 安装脚本替你点击；完成后再从 StikDebug 启动 MeloNX。%b\n' \
    "$COLOR_YELLOW" "$COLOR_RESET"
if [ -t 0 ]; then
    printf '%b  StikDebug 显示 JIT 成功后，回到这里按回车。%b' \
        "$COLOR_YELLOW" "$COLOR_RESET"
    read -r _
    complete_step
    printf '%b安装与 JIT 链路已由你确认完成。%b\n' "$COLOR_GREEN" "$COLOR_RESET"
else
    printf '%b[7/7] ⏸ 等待你在 iPhone 上完成 StikDebug JIT。%b\n' \
        "$COLOR_YELLOW" "$COLOR_RESET"
fi
