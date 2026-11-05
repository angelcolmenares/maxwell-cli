#!/bin/bash

# Exit immediately if any command fails
set -e

# 1. Check if dotnet is installed
if ! command -v dotnet &> /dev/null; then
    echo "Error: The .NET SDK is not installed. Please install it from https://dot.net"
    exit 1
fi

echo "Compiling and packaging maxwell-cli..."

# 2. Package the app
dotnet pack --configuration Release

echo "Configuring the global .NET tool..."

# 3. Uninstall previous version (using || true so the script doesn't crash if it wasn't installed)
dotnet tool uninstall --global maxwell-cli || true

# 4. Install the new version globally
dotnet tool install --global maxwell-cli --add-source ./nupkg

# 5. Clean up the temporary nupkg folder
if [ -d "./nupkg" ]; then
    rm -rf ./nupkg
fi

echo "--------------------------------------------------------"
echo "Installation completed successfully!"
echo "You can now run the command: maxwell-cli"
echo "--------------------------------------------------------"
echo "Note: If the command is not recognized immediately, please"
echo "ensure that '\$HOME/.dotnet/tools' is added to your PATH."
