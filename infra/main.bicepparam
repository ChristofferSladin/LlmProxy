using 'main.bicep'

// Non-secret defaults. The NVIDIA key is read from the NVIDIA_API_KEY environment variable at
// deploy time (deployment fails fast if it is unset), never committed here:
//   NVIDIA_API_KEY=nvapi-... az deployment group create --resource-group <rg> \
//     --template-file main.bicep --parameters main.bicepparam
// (A separate `--parameters nvidiaApiKey=...` override does not work: Bicep requires a
// .bicepparam file to assign every declared parameter — BCP258.)

param appName = 'llmproxy'
param location = 'swedencentral'
param nvidiaApiKey = readEnvironmentVariable('NVIDIA_API_KEY')
