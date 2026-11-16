# Serveur de jeu open-esport : image Linux sans carte graphique ni moteur de jeu.
# Construction (depuis le dossier netcode) :  docker build -t open-esport-server .
# Lancement à la main :  docker run --rm --name open-esport-test --cpus=2 -p 27015:27015/udp -v <dossier des cartes>:/data:ro open-esport-server --world /data/demo.world --fill-bots
# En vrai, c'est le matchmaking (OpenEsport.Matchmaker) qui lance un conteneur par partie, avec un nom explicite : open-esport-<mode>-<port>.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY OpenEsport.Core/ OpenEsport.Core/
COPY OpenEsport.Server/ OpenEsport.Server/
RUN dotnet publish OpenEsport.Server/OpenEsport.Server.csproj -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
# ramasse-miettes non concurrent, de poste de travail : pas de threads de GC qui se disputent les cœurs du conteneur
ENV DOTNET_gcServer=0 \
    DOTNET_gcConcurrent=0 \
    DOTNET_TieredPGO=1 \
    DOTNET_TC_QuickJitForLoops=1 \
    DOTNET_TieredCompilation=1 \
    DOTNET_GCgen0size=0x6000000
# le serveur écrit son balance.json par défaut dans /app s'il n'en trouve pas
RUN chown app /app
EXPOSE 27015/udp
USER app
ENTRYPOINT ["dotnet", "OpenEsport.Server.dll"]
