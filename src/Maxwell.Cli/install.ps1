# 1. Check if dotnet is installed
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET SDK is not installed. Please install it from https://dot.net"
    exit 1
}

Write-Host "Compiling and packaging maxwell-cli..." -ForegroundColor Cyan

# 2. Package the app using the .csproj configuration (outputs to ./nupkg/)
dotnet pack --configuration Release

Write-Host "Configuring the global .NET tool..." -ForegroundColor Cyan

# 3. Uninstall any previous version to prevent duplicate errors
dotnet tool uninstall --global maxwell-cli 2>$null

# 4. Install the new version globally using the package from the ./nupkg folder
dotnet tool install --global maxwell-cli --add-source ./nupkg

# 5. Clean up the temporary nupkg folder
if (Test-Path ./nupkg) {
    Remove-Item -Recurse -Force ./nupkg
}

Write-Host "Installation completed successfully!" -ForegroundColor Green
Write-Host "You can now run the command: maxwell-cli" -ForegroundColor Green
