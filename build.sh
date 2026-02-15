#!/usr/bin/env bash
set -euo pipefail

# ============================================================================
# PrivStack Dev Build — Core (Rust FFI) + Desktop (.NET) + Plugins (optional)
#
# Usage:
#   ./build.sh                         # Build core + desktop (debug)
#   ./build.sh --release               # Build core + desktop (release)
#   ./build.sh --run                   # Just launch (no build)
#   ./build.sh --run --rebuild         # Build everything, then launch
#   ./build.sh --run --with-plugins    # Incremental build + plugins, then launch
#   ./build.sh --skip-core             # Build desktop only (use existing native lib)
#   ./build.sh --skip-desktop          # Build core only
#   ./build.sh --test                  # Build + run all tests
#   ./build.sh --clean                 # Wipe artifacts, then full build
#   ./build.sh --fresh                 # Wipe DB/settings, then build + launch
#   ./build.sh --clean-plugins         # Remove plugins/ test directory
#
# --with-plugins builds all plugins from ../PrivStack-Plugins/ into plugins/
# so the app auto-discovers them at launch (via dev-time fallback path).
# ============================================================================

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
CORE_DIR="$REPO_ROOT/core"
DESKTOP_DIR="$REPO_ROOT/desktop"

# Defaults
MODE="debug"
SKIP_CORE=false
SKIP_DESKTOP=false
RUN_AFTER=false
RUN_TESTS=false
FRESH_DB=false
CLEAN=false
REBUILD=false
WITH_PLUGINS=false
CLEAN_PLUGINS=false
PERSIST_TEST_DATA=false

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Build the PrivStack core (Rust FFI) and desktop (.NET) projects.

Options:
  --release          Build in release mode (default: debug)
  --skip-core        Skip the Rust core build
  --skip-desktop     Skip the .NET desktop build
  --run              Launch the desktop app (skips build unless --rebuild or --with-plugins)
  --rebuild          Force full rebuild (ignore incremental caches)
  --with-plugins     Incrementally build changed plugins into plugins/ for integrated testing
  --clean-plugins    Remove the plugins/ test directory
  --test             Run tests after building (starts test containers, tears down after)
  --persist          Keep test containers and data running after --test for manual audit
  --clean            Wipe all build artifacts before building
  --fresh            Wipe all local databases and settings, start clean
  -h, --help         Show this help

Examples:
  ./build.sh                       # Full build (core + desktop, debug)
  ./build.sh --release             # Full build (release)
  ./build.sh --run                 # Just launch (no build)
  ./build.sh --run --rebuild       # Build everything, then launch
  ./build.sh --skip-core           # Desktop only (reuse existing native lib)
  ./build.sh --skip-core --run     # Rebuild desktop, then launch
  ./build.sh --run --with-plugins  # Incremental build + launch with plugins
  ./build.sh --run --with-plugins --rebuild  # Force full rebuild + launch
  ./build.sh --test                # Build + run all tests (containers auto-teardown)
  ./build.sh --test --persist      # Build + run tests, keep MinIO/MySQL running for audit
  ./build.sh --clean --run         # Nuke artifacts, rebuild, launch
  ./build.sh --fresh --run         # Nuke DB, rebuild, launch fresh
  ./build.sh --clean-plugins       # Remove plugins/ test directory
EOF
    exit 0
}

while [ $# -gt 0 ]; do
    case "$1" in
        --release)       MODE="release"; shift ;;
        --skip-core)     SKIP_CORE=true; shift ;;
        --skip-desktop)  SKIP_DESKTOP=true; shift ;;
        --run)           RUN_AFTER=true; shift ;;
        --rebuild)       REBUILD=true; shift ;;
        --with-plugins)  WITH_PLUGINS=true; shift ;;
        --clean-plugins) CLEAN_PLUGINS=true; shift ;;
        --test)          RUN_TESTS=true; shift ;;
        --persist)       PERSIST_TEST_DATA=true; shift ;;
        --clean)         CLEAN=true; shift ;;
        --fresh)         FRESH_DB=true; shift ;;
        -h|--help)       usage ;;
        *) echo "Unknown option: $1"; usage ;;
    esac
done

# --run alone (no --rebuild, --clean, --fresh, --test, --with-plugins) = skip all builds, just launch
# --run --with-plugins = incremental build (cargo/dotnet handle this natively)
if [ "$RUN_AFTER" = true ] && [ "$REBUILD" = false ] && \
   [ "$CLEAN" = false ] && [ "$FRESH_DB" = false ] && [ "$RUN_TESTS" = false ] && \
   [ "$WITH_PLUGINS" = false ]; then
    SKIP_CORE=true
    SKIP_DESKTOP=true
fi

# Build config
CARGO_PROFILE_FLAG=""
CARGO_TARGET_DIR="debug"
DOTNET_CONFIG="Debug"
if [ "$MODE" = "release" ]; then
    CARGO_PROFILE_FLAG="--release"
    CARGO_TARGET_DIR="release"
    DOTNET_CONFIG="Release"
fi

# Native library name (platform-dependent)
case "$(uname -s)" in
    Darwin)             LIB_NAME="libprivstack_ffi.dylib" ;;
    Linux)              LIB_NAME="libprivstack_ffi.so" ;;
    MINGW*|MSYS*|CYGWIN*) LIB_NAME="privstack_ffi.dll" ;;
    *) echo "Unsupported OS: $(uname -s)"; exit 1 ;;
esac

# ── Step 0a: Clean ──────────────────────────────────────────────
if [ "$CLEAN" = true ]; then
    echo "==> Cleaning build artifacts..."

    if [ -d "$CORE_DIR/target" ]; then
        echo "    Cleaning Rust core (cargo clean)..."
        cargo clean --manifest-path "$CORE_DIR/Cargo.toml" 2>/dev/null || true
    fi

    echo "    Cleaning .NET bin/obj..."
    find "$DESKTOP_DIR" -type d \( -name bin -o -name obj \) -exec rm -rf {} + 2>/dev/null || true

    echo "    Clean complete."
fi

# ── Step 0b: Fresh DB ──────────────────────────────────────────
if [ "$FRESH_DB" = true ]; then
    case "$(uname -s)" in
        Darwin)             DATA_DIR="$HOME/Library/Application Support/PrivStack" ;;
        Linux)              DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/PrivStack" ;;
        MINGW*|MSYS*|CYGWIN*) DATA_DIR="$LOCALAPPDATA/PrivStack" ;;
        *) echo "Unsupported OS for --fresh"; exit 1 ;;
    esac

    if [ -d "$DATA_DIR" ]; then
        echo "==> Wiping PrivStack data directory: $DATA_DIR"
        ls -la "$DATA_DIR"/ 2>/dev/null || true
        if [ -d "$DATA_DIR/workspaces" ]; then
            echo "    Workspaces:"
            ls -la "$DATA_DIR/workspaces"/ 2>/dev/null || true
        fi
        rm -rf "$DATA_DIR"
        echo "    Data directory removed."
    else
        echo "==> No data directory found at $DATA_DIR (already clean)."
    fi
fi

# ── Step 1: Build Rust core (FFI) ──────────────────────────────
if [ "$SKIP_CORE" = false ]; then
    echo "==> Building Rust core (privstack-ffi) [$MODE]..."
    cargo build -p privstack-ffi $CARGO_PROFILE_FLAG --manifest-path "$CORE_DIR/Cargo.toml"

    LIB_PATH="$CORE_DIR/target/$CARGO_TARGET_DIR/$LIB_NAME"
    if [ ! -f "$LIB_PATH" ]; then
        echo "ERROR: Native library not found at $LIB_PATH"
        exit 1
    fi

    # Show size
    if command -v du >/dev/null 2>&1; then
        echo "    Native library: $LIB_PATH ($(du -h "$LIB_PATH" | cut -f1))"
    else
        echo "    Native library: $LIB_PATH"
    fi
fi

# ── Step 1b: Ensure native lib is where .NET expects it ────────
# The .csproj references core/target/release/. For debug builds, copy the
# debug lib there so dotnet build can find it.
if [ "$SKIP_DESKTOP" = false ] && [ "$MODE" = "debug" ]; then
    RUST_RELEASE_DIR="$CORE_DIR/target/release"
    RUST_DEBUG_DIR="$CORE_DIR/target/debug"

    if [ -f "$RUST_DEBUG_DIR/$LIB_NAME" ]; then
        mkdir -p "$RUST_RELEASE_DIR"
        if [ ! -f "$RUST_RELEASE_DIR/$LIB_NAME" ] || \
           [ "$RUST_DEBUG_DIR/$LIB_NAME" -nt "$RUST_RELEASE_DIR/$LIB_NAME" ]; then
            echo "    Copying debug native lib to release dir (.csproj expects release path)..."
            cp "$RUST_DEBUG_DIR/$LIB_NAME" "$RUST_RELEASE_DIR/$LIB_NAME"
        fi
    fi
fi

# ── Step 2: Build .NET desktop ─────────────────────────────────
if [ "$SKIP_DESKTOP" = false ]; then
    echo "==> Building .NET desktop [$DOTNET_CONFIG]..."
    dotnet build "$DESKTOP_DIR/PrivStack.sln" -c "$DOTNET_CONFIG" --nologo -v quiet
    echo "    Desktop build complete."
fi

# ── Step 2b: Clean plugins ────────────────────────────────────
PLUGINS_OUTPUT_DIR="$REPO_ROOT/plugins"

TEST_DATA_DIR="$REPO_ROOT/test-data"

if [ "$CLEAN_PLUGINS" = true ] || [ "$CLEAN" = true ]; then
    if [ -d "$PLUGINS_OUTPUT_DIR" ]; then
        echo "==> Removing plugins/ test directory..."
        rm -rf "$PLUGINS_OUTPUT_DIR"
        echo "    Plugins directory removed."
    else
        echo "==> No plugins/ directory to clean."
    fi
    if [ -d "$TEST_DATA_DIR" ]; then
        echo "==> Removing test-data/ directory..."
        rm -rf "$TEST_DATA_DIR"
        echo "    Test data directory removed."
    fi
    # If only --clean-plugins was requested, exit
    if [ "$CLEAN_PLUGINS" = true ] && [ "$SKIP_CORE" = true ] && [ "$SKIP_DESKTOP" = true ] && \
       [ "$RUN_AFTER" = false ] && [ "$RUN_TESTS" = false ]; then
        echo "==> Done."
        exit 0
    fi
fi

# ── Step 2c: Build plugins (incremental) ─────────────────────
if [ "$WITH_PLUGINS" = true ]; then
    PLUGINS_SRC_DIR="$(cd "$REPO_ROOT/.." && pwd)/PrivStack-Plugins"

    if [ ! -d "$PLUGINS_SRC_DIR" ]; then
        echo "ERROR: PrivStack-Plugins directory not found at $PLUGINS_SRC_DIR"
        exit 1
    fi

    echo "==> Building plugins into $PLUGINS_OUTPUT_DIR..."
    mkdir -p "$PLUGINS_OUTPUT_DIR"

    PLUGIN_BUILT=0
    PLUGIN_SKIPPED=0
    PLUGIN_FAILED=0

    for plugin_csproj in "$PLUGINS_SRC_DIR"/PrivStack.Plugin.*/PrivStack.Plugin.*.csproj; do
        plugin_name=$(basename "${plugin_csproj%.csproj}")
        plugin_dir=$(dirname "$plugin_csproj")
        plugin_out="$PLUGINS_OUTPUT_DIR/$plugin_name"
        plugin_dll="$plugin_out/$plugin_name.dll"

        # Skip unchanged plugins: if the output DLL exists and no source file
        # (.cs, .csproj, .axaml, .xaml) is newer than it, skip the rebuild.
        if [ "$REBUILD" = false ] && [ -f "$plugin_dll" ]; then
            NEEDS_BUILD=false
            while IFS= read -r -d '' src_file; do
                if [ "$src_file" -nt "$plugin_dll" ]; then
                    NEEDS_BUILD=true
                    break
                fi
            done < <(find "$plugin_dir" \( -name "*.cs" -o -name "*.csproj" -o -name "*.axaml" -o -name "*.xaml" \) -not -path "*/bin/*" -not -path "*/obj/*" -print0)

            if [ "$NEEDS_BUILD" = false ]; then
                PLUGIN_SKIPPED=$((PLUGIN_SKIPPED + 1))
                continue
            fi
        fi

        echo "    Building $plugin_name..."
        if dotnet publish "$plugin_csproj" -c "$DOTNET_CONFIG" -o "$plugin_out" --nologo -v quiet 2>&1; then
            PLUGIN_BUILT=$((PLUGIN_BUILT + 1))
        else
            echo "    WARNING: Failed to build $plugin_name"
            PLUGIN_FAILED=$((PLUGIN_FAILED + 1))
        fi
    done

    echo "    Plugins — built: $PLUGIN_BUILT, up-to-date: $PLUGIN_SKIPPED, failed: $PLUGIN_FAILED"
    if [ "$PLUGIN_FAILED" -gt 0 ]; then
        echo "    WARNING: Some plugins failed to build. Continuing anyway..."
    fi
fi

# ── Step 3: Tests ──────────────────────────────────────────────
if [ "$RUN_TESTS" = true ]; then
    COMPOSE_FILE="$REPO_ROOT/docker-compose.test.yml"
    COMPOSE_UP=false

    # Start test containers if compose file exists
    if [ -f "$COMPOSE_FILE" ]; then
        echo "==> Starting test containers (MinIO + MySQL)..."
        # Start persistent services first, then run the init container separately.
        # minio-setup exits after creating the bucket, which causes --wait to
        # return non-zero and trip set -e.
        docker compose -f "$COMPOSE_FILE" up -d --wait minio mysql
        docker compose -f "$COMPOSE_FILE" run --rm minio-setup
        COMPOSE_UP=true
    fi

    TEST_EXIT=0

    echo "==> Running Rust tests..."
    cargo test --manifest-path "$CORE_DIR/Cargo.toml" $CARGO_PROFILE_FLAG || TEST_EXIT=$?

    echo "==> Running .NET tests..."
    dotnet test "$DESKTOP_DIR/PrivStack.sln" -c "$DOTNET_CONFIG" --nologo -v quiet || TEST_EXIT=$?

    # Express integration tests (if config exists)
    WEB_ROOT="$(cd "$REPO_ROOT/.." && pwd)/PrivStack-Web"
    if [ -f "$WEB_ROOT/api/vitest.integration.config.js" ]; then
        echo "==> Running Express integration tests..."
        (cd "$WEB_ROOT" && npx vitest run --config api/vitest.integration.config.js) || TEST_EXIT=$?
    fi

    # Teardown unless --persist
    if [ "$COMPOSE_UP" = true ]; then
        if [ "$PERSIST_TEST_DATA" = true ]; then
            echo "==> --persist: leaving test containers running."
            echo "    MinIO console: http://localhost:9001  (privstack-test / privstack-test-secret)"
            echo "    MySQL:         localhost:3307          (root / test, db: privstack_test)"
            echo "    To tear down:  docker compose -f $COMPOSE_FILE down -v"
        else
            echo "==> Tearing down test containers..."
            docker compose -f "$COMPOSE_FILE" down -v
        fi
    fi

    if [ "$TEST_EXIT" -ne 0 ]; then
        echo "==> Tests failed (exit code $TEST_EXIT)."
        exit "$TEST_EXIT"
    fi
fi

# ── Step 4: Run ────────────────────────────────────────────────
if [ "$RUN_AFTER" = true ]; then
    if [ "$WITH_PLUGINS" = true ]; then
        # Isolated test instance — separate data directory from live
        mkdir -p "$TEST_DATA_DIR"
        echo "==> Launching PrivStack Desktop (test mode — isolated data at $TEST_DATA_DIR)..."
        PRIVSTACK_DATA_DIR="$TEST_DATA_DIR" \
            dotnet run --project "$DESKTOP_DIR/PrivStack.Desktop/PrivStack.Desktop.csproj" -c "$DOTNET_CONFIG" --no-build
    else
        echo "==> Launching PrivStack Desktop..."
        dotnet run --project "$DESKTOP_DIR/PrivStack.Desktop/PrivStack.Desktop.csproj" -c "$DOTNET_CONFIG" --no-build
    fi
fi

echo "==> Done."
