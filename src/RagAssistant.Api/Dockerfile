FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props RagAssistant.slnx ./
COPY src/RagAssistant.Domain/*.csproj src/RagAssistant.Domain/
COPY src/RagAssistant.Application/*.csproj src/RagAssistant.Application/
COPY src/RagAssistant.Infrastructure/*.csproj src/RagAssistant.Infrastructure/
COPY src/RagAssistant.Api/*.csproj src/RagAssistant.Api/
RUN dotnet restore src/RagAssistant.Api/RagAssistant.Api.csproj
COPY src/ src/
RUN dotnet publish src/RagAssistant.Api/RagAssistant.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY samples/ /app/samples/
RUN mkdir -p /data/uploads && chown -R app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    Demo__SamplesPath=/app/samples
EXPOSE 8080
ENTRYPOINT ["dotnet", "RagAssistant.Api.dll"]
