{
  description = "BTCPay Server Fireblocks plugin development environment and package";

  inputs = {
    # This revision provides SDK 10.0.403, matching global.json and BTCPay v2.4.7.
    nixpkgs.url = "github:NixOS/nixpkgs/1c89083e451e891152365b799595cf7790a43325";
    # Keep this revision aligned with the submodules/btcpayserver gitlink (v2.4.7).
    btcpayserver = {
      url = "github:btcpayserver/btcpayserver/2868deedd9d911b715fec7306ea9cada36d86bff";
      flake = false;
    };
  };

  outputs = { self, nixpkgs, btcpayserver }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
    in
    {
      packages = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
          plugin = pkgs.buildDotnetModule rec {
            pname = "BTCPayServer.Plugins.Fireblocks";
            version = "0.1.0";
            src = self;

            projectFile = "src/${pname}/${pname}.csproj";
            nugetDeps = ./nix/deps.json;
            dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
            runtimeId = null;
            useAppHost = false;
            executables = [ ];
            installPath = "${placeholder "out"}/share/btcpayserver/Plugins/${pname}";

            # Flake sources do not include Git submodule contents. Supply the
            # same pinned host revision at the ProjectReference's expected path.
            postPatch = ''
              mkdir --parents submodules
              rm -rf submodules/btcpayserver
              cp --recursive ${btcpayserver} submodules/btcpayserver
              chmod --recursive u+w submodules/btcpayserver
            '';

            meta = {
              description = "Fireblocks integration scaffold for BTCPay Server";
              homepage = "https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks";
              license = nixpkgs.lib.licenses.mit;
              platforms = nixpkgs.lib.platforms.linux;
            };
          };
        in
        {
          default = plugin;
          fetch-deps = plugin.fetch-deps;
        });

      checks = forAllSystems (system: {
        package = self.packages.${system}.default;
      });

      devShells = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
        in
        {
          default = pkgs.mkShell {
            packages = [
              pkgs.dotnetCorePackages.sdk_10_0
              pkgs.git
              pkgs.python3
            ];

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";

            shellHook = ''
              test "$(dotnet --version)" = "10.0.403"
            '';
          };
        });
    };
}
