# ArcGIS Pro 3.3 AI Add-in Skeleton

This repository contains a starter ArcGIS Pro 3.3 add-in for a Chinese natural-language cartography and spatial-analysis assistant.

## Requirements

- ArcGIS Pro 3.3
- ArcGIS Pro SDK for .NET
- Visual Studio 2022 17.8 or later
- .NET 8 runtime/SDK
- `DEEPSEEK_API_KEY` in the environment or a local `.env` file

The default ArcGIS Pro path is configured in `Directory.Build.props`:

```xml
<ArcGISProInstallDir>D:\Arcgis pro</ArcGISProInstallDir>
```

Change it if ArcGIS Pro is installed elsewhere.

## Project Shape

- `ArcGisAiAssistant.AddIn`: the ArcGIS Pro add-in project.
- `Config.daml`: ribbon button and dock pane registration.
- `Ui`: WPF dock pane and view model.
- `Ai`: OpenAI client, prompt orchestration, and intent routing.
- `ArcGis`: ArcGIS map/geoprocessing execution services.
- `Models`: shared DTOs for request context, tool plans, and execution results.

## First Run

1. Set `DEEPSEEK_API_KEY`.
2. Open `ArcGisAiAssistant.sln` in Visual Studio.
3. Confirm ArcGIS Pro SDK is installed.
4. Build the solution.
5. Find the generated add-in package under `src\ArcGisAiAssistant.AddIn\bin\x64\Debug\ArcGisAiAssistant.AddIn.esriAddinX`.
6. In Visual Studio, choose the `ArcGIS Pro` debug profile and press F5.

ArcGIS Pro SDK targets create and register the generated add-in package under:

```text
%USERPROFILE%\Documents\ArcGIS\AddIns\ArcGISPro\{D0B26B57-6F64-4479-8F64-90BB3B77BEA0}\
```

The skeleton intentionally routes AI output through JSON tool plans instead of executable code.

## API Key File

For local development, edit the ignored `.env` file at the repository root:

```text
DEEPSEEK_API_KEY=your-api-key
DEEPSEEK_MODEL=deepseek-v4-pro
```

The add-in reads the Windows environment variable first, then `.env`. Keep real keys out of git; `.env.example` is the committed template.

The DeepSeek client uses the OpenAI-compatible endpoint `https://api.deepseek.com/chat/completions` and JSON output mode.
