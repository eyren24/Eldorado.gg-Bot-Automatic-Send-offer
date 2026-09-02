FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["EldoradoApp.Contracts/EldoradoApp.Contracts.csproj", "EldoradoApp.Contracts/"]
COPY ["EldoradoApp.Server/EldoradoApp.Server.csproj", "EldoradoApp.Server/"]
COPY ["EldoradoApp/Models/LicenseInfo.cs", "EldoradoApp/Models/"]
COPY ["EldoradoApp/Services/Licensing/Base32.cs", "EldoradoApp/Services/Licensing/"]
COPY ["EldoradoApp/Services/Licensing/LicenseCodec.cs", "EldoradoApp/Services/Licensing/"]
RUN dotnet restore "EldoradoApp.Server/EldoradoApp.Server.csproj"
COPY . .
RUN dotnet publish "EldoradoApp.Server/EldoradoApp.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "EldoradoApp.Server.dll"]
