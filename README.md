# Pocket Wuque

Pocket Wuque is a Windows app for putting a picture or animated GIF on a compatible Wuque keyboard dial over Bluetooth LE. It previews the processed dial before sending it and shows whether the keyboard confirmed the transfer.

## For everyday use

The GitHub source-code download is not an installable app. A ready-to-run Windows release should be published as a ZIP under the repository's **Releases** page. Once you have that release ZIP:

1. Right-click the ZIP and choose **Extract All**.
2. Open the extracted folder and double-click **WuqueProbe.exe**.
3. If Windows asks whether to allow Bluetooth access, choose **Yes**. Only download the app from this project's GitHub Releases page.

If there is no ZIP under **Releases** yet, use the developer run steps below or ask the person maintaining the project to make a release package.

## Requirements

- Windows 10 version 2004 (build 19041) or newer.
- A working Bluetooth LE adapter and a compatible Wuque keyboard nearby, awake, and advertising.
- To run from source: the .NET 8 SDK. The app does not need Android, JADX, or the original keyboard companion APK.

## Run from source

1. Install the **.NET 8 SDK** from [Microsoft's .NET 8 download page](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Download this repository's source ZIP from GitHub (**Code → Download ZIP**) and extract it.
3. Open the extracted `wuque-probe` folder in File Explorer.
4. Click the folder address bar, type `powershell`, and press **Enter**.
5. In the PowerShell window, type `dotnet run` and press **Enter**. The first run may take a few minutes while Windows downloads build components.
6. Leave the PowerShell window open while using the app. Close the app window when finished; the PowerShell window can then be closed.

If Windows says that `dotnet` is not recognized, install the .NET 8 SDK and reopen PowerShell.

## Make a ready-to-run download

The project maintainer can create the ZIP for the **For everyday use** steps from PowerShell in the project folder. This includes the .NET runtime, so the person using the ZIP does not need to install the SDK:

```powershell
dotnet publish .\WuqueProbe.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
Compress-Archive -Path .\publish\win-x64\* -DestinationPath .\publish\Pocket-Wuque-windows-x64.zip -Force
```

Upload `publish\Pocket-Wuque-windows-x64.zip` to a GitHub Release. The person using the app should download that ZIP from **Releases**, extract it, and start `WuqueProbe.exe`.

## Send a picture or GIF

1. Turn on the keyboard and make sure Bluetooth is enabled on the PC.
2. In Pocket Wuque, click **Scan**. Find your keyboard by its name, click its row, then click **Connect selected**. If it does not appear, wake the keyboard and scan again.
3. Click **Choose image or GIF…** and select a PNG, JPEG, BMP, or GIF.
4. In **Layout**, choose **Fit entire image/GIF** to keep the whole picture visible (unused space becomes black), or **Crop to fill** to fill the dial while trimming edges. Drag the crop frame to move it; drag its lower-right handle to resize it. Use the **Size** slider to scale the fit preview.
5. Check the **Exact dial preview (320×172)**. For a GIF, use the preview and speed control to check the animation. GIF sending requires a connected generation-3 keyboard; click **Query version** after connecting if needed.
6. Click **Transfer dial** once. Keep the app and keyboard nearby and connected until the app says the keyboard confirmed success. A large 50-frame GIF took about **5 minutes** in a successful test. Do not turn off the keyboard or disconnect during a transfer; if interrupted, reconnect and start again.

The progress display tracks data sent; the app only reports completion after the keyboard's final confirmation. If an error appears, reconnect the keyboard and try again. If it repeats, use **Export support bundle** and share the file with the project maintainer.

## Privacy

Nothing is uploaded automatically. An exported support bundle is a local JSON file. It can include the keyboard's Bluetooth address, nearby device advertisements, Windows/Bluetooth details, event logs, and the selected image's file path. Review it before sharing; it does not need to include the image itself to be useful.

## Project notes

- [Protocol and validation notes](docs/PROTOCOL-NOTES.md) explain the verified BLE format and transfer behavior for developers.
- [`.gitignore`](.gitignore) keeps local build output, logs, and private support bundles out of source control.
