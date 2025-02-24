﻿# Installing PowerShell: https://docs.microsoft.com/powershell/scripting/install/installing-powershell

param(
	[Parameter(Mandatory=$true)]
	[string]$DockerfilePath,
	[Parameter(Mandatory=$true)]
	[string]$HttpStartPath,
    [string]$ImageName="dfapp",
	[string]$ContainerName="app",
	[switch]$NoSetup=$false,
	[switch]$NoValidation=$false,
	[string]$AzuriteVersion="3.15.0",
	[int]$Sleep=30
)

$ErrorActionPreference = "Stop"

if ($NoSetup -eq $false) {
	# Build the docker image first, since that's the most critical step
	Write-Host "Building sample app Docker container from '$DockerfilePath'..." -ForegroundColor Yellow
	docker build -f $DockerfilePath -t $ImageName --progress plain $PSScriptRoot/../../

	# Next, download and start the Azurite emulator Docker image
	Write-Host "Pulling down the mcr.microsoft.com/azure-storage/azurite:$AzuriteVersion image..." -ForegroundColor Yellow
	docker pull "mcr.microsoft.com/azure-storage/azurite:${AzuriteVersion}"

	Write-Host "Starting Azurite storage emulator using default ports..." -ForegroundColor Yellow
	docker run --name 'azurite' -p 10000:10000 -p 10001:10001 -p 10002:10002 -d "mcr.microsoft.com/azure-storage/azurite:${AzuriteVersion}"

	# Finally, start up the smoke test container, which will connect to the Azurite container
	docker run --name $ContainerName -p 8080:80 -it --add-host=host.docker.internal:host-gateway -d `
		--env 'AzureWebJobsStorage=UseDevelopmentStorage=true;DevelopmentStorageProxyUri=http://host.docker.internal' `
		--env 'WEBSITE_HOSTNAME=localhost:8080' `
		$ImageName
}

if ($sleep -gt  0) {
	# The container needs a bit more time before it can start receiving requests
	Write-Host "Sleeping for $Sleep seconds to let the container finish initializing..." -ForegroundColor Yellow
	Start-Sleep -Seconds $Sleep
}

# Check to see what containers are running
docker ps

try {
	# Make sure the Functions runtime is up and running
	$pingUrl = "http://localhost:8080/admin/host/ping"
	Write-Host "Pinging app at $pingUrl to ensure the host is healthy" -ForegroundColor Yellow
	Invoke-RestMethod -Method Post -Uri "http://localhost:8080/admin/host/ping"

	if ($NoValidation -eq $false) {
		# Note that any HTTP protocol errors (e.g. HTTP 4xx or 5xx) will cause an immediate failure
		$startOrchestrationUri = "http://localhost:8080/$HttpStartPath"
		Write-Host "Starting a new orchestration instance via POST to $startOrchestrationUri..." -ForegroundColor Yellow

		$result = Invoke-RestMethod -Method Post -Uri $startOrchestrationUri
		Write-Host "Started orchestration with instance ID '$($result.id)'!" -ForegroundColor Yellow
		Write-Host "Waiting for orchestration to complete..." -ForegroundColor Yellow

		$retryCount = 0
		$success = $false
		$statusUrl = $result.statusQueryGetUri

		while ($retryCount -lt 15) {
			$result = Invoke-RestMethod -Method Get -Uri $statusUrl
			$runtimeStatus = $result.runtimeStatus
			Write-Host "Orchestration is $runtimeStatus" -ForegroundColor Yellow

			if ($result.runtimeStatus -eq "Completed") {
				$success = $true
				break
			}

			Start-Sleep -Seconds 1
			$retryCount = $retryCount + 1
		}
	}

	if ($success -eq $false) {
		throw "Orchestration didn't complete in time! :("
	}
} catch {
	Write-Host "An error occurred:" -ForegroundColor Red
	Write-Host $_ -ForegroundColor Red

	# Dump the docker logs to make debugging the issue easier
	Write-Host "Below are the docker logs for the app container:" -ForegroundColor Red
	docker logs $ContainerName

	# Rethrow the original exception
	throw
}

Write-Host "Success!" -ForegroundColor Green