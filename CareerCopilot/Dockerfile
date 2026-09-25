# ==========================================
# 1. ETAPA DE COMPILACIÓN (BUILD & PUBLISH)
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copiar csproj y restaurar dependencias
COPY ["CareerCopilot.csproj", "./"]
RUN dotnet restore "CareerCopilot.csproj"

# Copiar el resto del código y compilar en modo Release
COPY . .
RUN dotnet publish "CareerCopilot.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ==========================================
# 2. ETAPA DE EJECUCIÓN (RUNTIME + TYPST)
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Instalar dependencias del sistema, fuentes estándar y Typst
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    xz-utils \
    fonts-liberation \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

# Descargar e instalar el binario oficial de Typst en /usr/local/bin
ARG TYPST_VERSION=0.11.1
RUN curl -fsSL "https://github.com/typst/typst/releases/download/v${TYPST_VERSION}/typst-x86_64-unknown-linux-musl.tar.xz" \
    | tar -xJ -C /tmp \
    && mv /tmp/typst-x86_64-unknown-linux-musl/typst /usr/local/bin/typst \
    && chmod +x /usr/local/bin/typst \
    && rm -rf /tmp/typst*

# Copiar la aplicación compilada
COPY --from=build /app/publish .

# Crear la carpeta de salida de PDFs y dar permisos
RUN mkdir -p /app/GeneratedCVs

# Exponer los puertos HTTP de ASP.NET Core
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "CareerCopilot.dll"]