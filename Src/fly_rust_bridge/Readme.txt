
INITIAL SETUP
Download and install Rust from: https://rustup.rs/
Ensure you have Visual Studio Build Tools installed with the Desktop development with C++ workload.

CREATE THE PROJECT
Open your terminal or command prompt and run these commands:
cargo new --lib fly_rust_bridge
cd fly_rust_bridge

EDIT CONFIGURATION FILE
Open the file named Cargo.toml in your project folder 

EDIT THE RUST CODE
Open the file src/lib.rs 

ADD BUILD TARGETS
Run these commands once to prepare your computer for building both versions:
rustup target add x86_64-pc-windows-msvc
rustup target add aarch64-pc-windows-msvc

BUILD COMMANDS
Run these commands to generate the DLL files:

For standard PC (x64):
cargo build --release --target x86_64-pc-windows-msvc

For ARM devices (ARM64):
cargo build --release --target aarch64-pc-windows-msvc

HOW TO USE IN C#
Place the generated dll files in your FlyPhotos project in their respecive folders

Use these DllImports in your C# code:

CLEANING UP
If you want to delete all build files to save space or fix errors, run:
cargo clean