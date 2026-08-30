{
  description = "mailcoded — local-first email engine";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };
        aotDeps = with pkgs; [ clang lld zlib openssl icu ];
      in
      {
        devShells.default = pkgs.mkShell {
          packages = with pkgs; [
            dotnet-sdk_10
            sqlite
            docker-client
            nodejs_22
            gh
          ] ++ aotDeps;

          shellHook = ''
            export DOTNET_CLI_TELEMETRY_OPTOUT=1
            export DOTNET_NOLOGO=1
            export DOTNET_ROOT="${pkgs.dotnet-sdk_10}/share/dotnet"
            # Native AOT links with clang; point it at the nix toolchain.
            export CppCompilerAndLinker=clang
            export LD_LIBRARY_PATH="${pkgs.lib.makeLibraryPath aotDeps}:$LD_LIBRARY_PATH"
            echo "mailcoded devShell — dotnet $(dotnet --version)"
          '';
        };
      });
}
