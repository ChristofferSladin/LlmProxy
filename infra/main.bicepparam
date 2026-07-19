using 'main.bicep'

// Non-secret defaults. Supply nvidiaApiKey at deploy time instead, e.g.:
//   az deployment group create --resource-group <rg> --template-file main.bicep \
//     --parameters main.bicepparam --parameters nvidiaApiKey=$NVIDIA_API_KEY

param appName = 'llmproxy'
param location = 'swedencentral'
