FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["CareerCopilot.csproj", "./"]
RUN dotnet restore "CareerCopilot.csproj"

COPY . .
RUN dotnet publish "CareerCopilot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    xz-utils \
    fonts-dejavu-core \
    fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL "https://github.com/typst/typst/releases/download/v0.11.1/typst-x86_64-unknown-linux-musl.tar.xz" \
    | tar -xJ -C /tmp \
    && mv /tmp/typst-x86_64-unknown-linux-musl/typst /usr/local/bin/typst \
    && chmod +x /usr/local/bin/typst \
    && rm -rf /tmp/typst*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CareerCopilot.dll"]