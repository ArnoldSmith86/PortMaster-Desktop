#!/bin/bash
/home/mint/Projects/Gaming/PortMaster-Desktop/tools/dotnet9/dotnet run -c Release -p PortMasterDesktop -- --test --ports 2>&1 | grep -i "duck.dodge\|duckdodge" | head -5
