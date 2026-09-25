# -------------------------------------------------------------
# Etapa 1: Build y Publish
# -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copiar csproj y restaurar dependencias
COPY ["CareerCopilot.csproj", "./"]
RUN dotnet restore "CareerCopilot.csproj"

# Copiar el resto del código y compilar en modo Release
COPY . .
RUN dotnet publish "CareerCopilot.csproj" -c Release -o /app/publish /p:UseAppHost=false

# -------------------------------------------------------------
# Etapa 2: Runtime Final
# -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS final
WORKDIR /app

# Instalar dependencias del sistema y tipografías para Typst
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    xz-utils \
    fonts-dejavu-core \
    fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

# Descargar e instalar el binario estático de Typst
RUN curl -fsSL "https://github.com/typst/typst/releases/download/v0.11.1/typst-x86_64-unknown-linux-musl.tar.xz" \
    | tar -xJ -C /tmp \
    && mv /tmp/typst-x86_64-unknown-linux-musl/typst /usr/local/bin/typst \
    && chmod +x /usr/local/bin/typst \
    && rm -rf /tmp/typst*

# Copiar artefactos publicados de la aplicación
COPY --from=build /app/publish .

# Puerto por defecto para Render
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CareerCopilot.dll"]