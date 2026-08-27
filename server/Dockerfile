# The game server, as a container.
#
# ══ WHY THE CONTEXT IS THE REPOSITORY ROOT ════════════════════════════════════
#
# IdleExplorers.Rules compiles files that live in Assets/Scripts/Rules — the same
# files Unity compiles. That is the whole anti-drift design, and it means a build
# context of server/ alone cannot see half its own source.
#
# Build from the root:
#     docker build -f server/Dockerfile -t idle-explorers-api .
#
# ══ WHY CONTENT IS COPIED IN ══════════════════════════════════════════════════
#
# The twelve StreamingAssets files are the authored game, and the server reads them
# rather than a database export. Baking them into the image makes the image the unit
# of deployment: the content and the code that validates it ship together and cannot
# be half-updated.

# ── Build ─────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so a change to source does not invalidate the restore layer.
# Worth the extra COPY lines: restore is the slow half of a cold build.
COPY server/IdleExplorers.slnx                                            server/
COPY server/src/IdleExplorers.Rules/IdleExplorers.Rules.csproj            server/src/IdleExplorers.Rules/
COPY server/src/IdleExplorers.Content/IdleExplorers.Content.csproj        server/src/IdleExplorers.Content/
COPY server/src/IdleExplorers.Api/IdleExplorers.Api.csproj                server/src/IdleExplorers.Api/

RUN dotnet restore server/src/IdleExplorers.Api/IdleExplorers.Api.csproj

# The shared rules tree, which lives outside server/ on purpose.
COPY Assets/Scripts/Rules/ Assets/Scripts/Rules/
COPY server/src/           server/src/

RUN dotnet publish server/src/IdleExplorers.Api/IdleExplorers.Api.csproj \
    -c Release -o /app --no-restore

# ── Run ───────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app ./

# Beside the binary, which is where ContentCache looks first.
COPY Assets/StreamingAssets/ ./content/

# Not root. The process needs to read its own directory and open sockets, and
# nothing else — a container that can write its own binary is a container where a
# remote code execution becomes persistent.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin explorer \
 && chown -R explorer:explorer /app
USER explorer

ENV PORT=8080 \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    IDLE_EXPLORERS_CONTENT=/app/content \
    DOTNET_gcServer=1

EXPOSE 8080

# Liveness only. /readyz touches the database and is what the platform should poll
# for routing decisions — a container that cannot reach Postgres is still alive and
# killing it would turn a database blip into a restart loop.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/app/IdleExplorers.Api", "--healthcheck"]

ENTRYPOINT ["dotnet", "IdleExplorers.Api.dll"]
