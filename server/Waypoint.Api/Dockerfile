FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Waypoint.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Development only: CLAUDE_CLI=true adds the claude CLI so the Coach can run on your Claude subscription.
ARG CLAUDE_CLI=false
RUN if [ "$CLAUDE_CLI" = "true" ]; then \
      apt-get update && apt-get install -y --no-install-recommends nodejs npm ca-certificates \
      && npm install -g @anthropic-ai/claude-code \
      && apt-get clean && rm -rf /var/lib/apt/lists/*; \
    fi
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Waypoint.Api.dll"]
