#!/usr/bin/env bash
# Build du plugin contre les paquets NuGet Jellyfin officiels.
#
#   ./build.sh            build (dans un conteneur SDK .NET 10 si dotnet n'est pas installé)
#   ./build.sh --deploy   build + copie sur le serveur + redémarrage de Jellyfin (variables JF_* ci-dessous)
#
# Variables (optionnelles) :
#   BUILD_ENGINE   docker | podman | local   (défaut : local si dotnet présent, sinon docker, sinon podman)
#   BUILD_NET      host | bridge             (défaut : host, le conteneur utilise le DNS de la machine)
#   WL_DNS         serveur DNS forcé pour le conteneur, ex. 10.10.0.1
#   SDK_IMAGE      image du SDK              (défaut : mcr.microsoft.com/dotnet/sdk:10.0)
#   JF_SSH         hôte SSH du serveur Jellyfin, ex. root@tower           (requis pour --deploy)
#   JF_PLUGINS     dossier plugins côté hôte, ex. /mnt/user/appdata/jellyfin/plugins   (requis pour --deploy)
#   JF_CONTAINER   nom du conteneur Jellyfin (défaut : Jellyfin)
#   WL_BUILD_ARGS  arguments supplémentaires pour dotnet build
set -euo pipefail
cd "$(dirname "$0")"

SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
DEPLOY=0
[ "${1:-}" = "--deploy" ] && DEPLOY=1

VERSION=$(grep -oP '(?<="version": ")[^"]+' meta.json)
DLL=src/Jellyfin.Plugin.Watchlists/bin/Release/net10.0/Jellyfin.Plugin.Watchlists.dll
OUT=dist/Watchlists_$VERSION

# ------------------------------------------------------------------ compilation
do_build() {
    # Repart de zéro : une DLL en cache dans obj/ peut être recopiée sans réembarquer Web/*.
    rm -rf src/Jellyfin.Plugin.Watchlists/bin src/Jellyfin.Plugin.Watchlists/obj
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    # shellcheck disable=SC2086
    dotnet build src/Jellyfin.Plugin.Watchlists -c Release --no-incremental -nologo -v minimal ${WL_BUILD_ARGS:-}
}

# Appel interne depuis le conteneur : compilation seule, la suite tourne sur l'hôte.
if [ "${WL_IN_CONTAINER:-0}" = "1" ]; then
    # Échec immédiat si le conteneur n'a pas de DNS (sinon NuGet réessaie pendant 4 min).
    if ! getent hosts api.nuget.org >/dev/null; then
        echo "ERREUR : le conteneur ne résout pas api.nuget.org (DNS). Contenu de /etc/resolv.conf :" >&2
        cat /etc/resolv.conf >&2
        echo "Essayer : WL_DNS=<ip de ton résolveur> ./build.sh   ou   BUILD_NET=bridge ./build.sh" >&2
        exit 1
    fi
    do_build
    exit 0
fi

ENGINE="${BUILD_ENGINE:-}"
if [ -z "$ENGINE" ]; then
    if command -v dotnet >/dev/null; then ENGINE=local
    elif command -v docker >/dev/null; then ENGINE=docker
    elif command -v podman >/dev/null; then ENGINE=podman
    else echo "ERREUR : ni dotnet, ni podman, ni docker trouvés." >&2; exit 1
    fi
fi

if [ "$ENGINE" = "local" ]; then
    do_build
else
    RUN=("$ENGINE")
    if [ "$ENGINE" = "docker" ]; then
        # Démon inaccessible sans droits : on passe par sudo. --user garde les fichiers produits à l'utilisateur.
        if ! docker info >/dev/null 2>&1; then
            RUN=(sudo docker)
            if ! sudo docker info >/dev/null 2>&1; then
                echo "ERREUR : démon Docker arrêté ou inaccessible — sudo systemctl enable --now docker" >&2; exit 1
            fi
        fi
        USER_OPTS=(--user "$(id -u):$(id -g)")
    else
        USER_OPTS=(--userns=keep-id)
    fi

    # Réseau de l'hôte par défaut : le conteneur utilise le même DNS que la machine
    # (évite les échecs de résolution du réseau bridge / rootless). BUILD_NET=bridge pour revenir au défaut.
    # --dns est incompatible avec --network host : WL_DNS force donc le réseau bridge.
    NET_OPTS=()
    if [ -n "${WL_DNS:-}" ]; then BUILD_NET=bridge; NET_OPTS=(--dns "$WL_DNS")
    elif [ "${BUILD_NET:-host}" = "host" ]; then NET_OPTS=(--network host)
    fi

    # Cache NuGet persistant dans le projet : les paquets ne sont téléchargés qu'une fois.
    mkdir -p .cache/nuget .cache/home
    echo ">> Build dans $SDK_IMAGE via ${RUN[*]} (réseau : ${BUILD_NET:-host}${WL_DNS:+, DNS $WL_DNS})"
    "${RUN[@]}" run --rm "${NET_OPTS[@]}" \
        -v "$PWD":/src:Z -w /src \
        "${USER_OPTS[@]}" \
        -e HOME=/src/.cache/home \
        -e DOTNET_CLI_HOME=/src/.cache/home \
        -e NUGET_PACKAGES=/src/.cache/nuget \
        -e WL_IN_CONTAINER=1 \
        -e WL_BUILD_ARGS="${WL_BUILD_ARGS:-}" \
        "$SDK_IMAGE" bash build.sh
fi

# ------------------------------------------------------------------ vérification + paquet
[ -f "$DLL" ] || { echo "ERREUR : $DLL absent, le build a échoué." >&2; exit 1; }

# Les pages web sont embarquées dans la DLL : on vérifie que ce sont bien les versions du dépôt.
for check in "Web/watchlists.html:class=\"wl-app\"" "Web/header.js:wl-own-tray" "Web/config.html:WatchlistsConfigPage"; do
    file=${check%%:*}; marker=${check#*:}
    if ! grep -q "$marker" "src/Jellyfin.Plugin.Watchlists/$file"; then
        echo "ERREUR : marqueur « $marker » absent de $file (fichier source pas à jour ?)" >&2; exit 1
    fi
    if ! grep -qa "$marker" "$DLL"; then
        echo "ERREUR : $file n'est pas embarqué dans la DLL (marqueur « $marker » introuvable)" >&2; exit 1
    fi
done

rm -rf dist && mkdir -p "$OUT"
cp "$DLL" meta.json "$OUT/"
echo "OK -> $OUT ($(sha256sum "$OUT/Jellyfin.Plugin.Watchlists.dll" | cut -c1-12))"

# ------------------------------------------------------------------ déploiement (optionnel)
if [ "$DEPLOY" = "1" ]; then
    : "${JF_SSH:?JF_SSH requis, ex. JF_SSH=root@tower}"
    : "${JF_PLUGINS:?JF_PLUGINS requis, ex. JF_PLUGINS=/mnt/user/appdata/jellyfin/plugins}"
    JF_CONTAINER="${JF_CONTAINER:-Jellyfin}"
    echo ">> Déploiement sur $JF_SSH:$JF_PLUGINS/Watchlists_$VERSION"
    # Supprime toutes les versions du plugin (deux dossiers Watchlists = ancienne DLL potentiellement chargée).
    ssh "$JF_SSH" "rm -rf '$JF_PLUGINS'/Watchlists_* && mkdir -p '$JF_PLUGINS/Watchlists_$VERSION'"
    scp -q "$OUT/Jellyfin.Plugin.Watchlists.dll" "$OUT/meta.json" "$JF_SSH:$JF_PLUGINS/Watchlists_$VERSION/"
    LOCAL_SUM=$(sha256sum "$OUT/Jellyfin.Plugin.Watchlists.dll" | cut -d' ' -f1)
    REMOTE_SUM=$(ssh "$JF_SSH" "sha256sum '$JF_PLUGINS/Watchlists_$VERSION/Jellyfin.Plugin.Watchlists.dll'" | cut -d' ' -f1)
    [ "$LOCAL_SUM" = "$REMOTE_SUM" ] || { echo "ERREUR : DLL copiée différente de la DLL buildée." >&2; exit 1; }
    # La DLL est chargée en mémoire au démarrage : redémarrage obligatoire.
    ssh "$JF_SSH" "docker restart '$JF_CONTAINER'" >/dev/null
    echo "OK -> $JF_CONTAINER redémarré avec Watchlists $VERSION. Recharger le client web (Ctrl+F5)."
fi