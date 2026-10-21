namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions

/// Compares the running build against published GitHub releases and installs
/// the latest stable release when the user requests an update.
module UpdateChecker =

    [<Literal>]
    let CurrentVersion = "1.3.3"

    [<Literal>]
    let ReleasesApiUrl = "https://api.github.com/repos/NODIX-TECH/DLSS-5-MANAGER/releases"

    type UpdateResult =
        { HasUpdate: bool
          LatestVersion: string
          InstallerUrl: string
          InstallerSha256: string
          Message: string }

    let private client =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromSeconds(12.0)
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Manager-Updater/1.0")
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")
        c

    let private downloadClient =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromHours(2.0)
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Manager-Updater/1.0")
        c

    /// "v1.2.3" / "1.2.3-beta" -> [1; 2; 3]
    let private parseVersion (raw: string) : int list =
        if String.IsNullOrWhiteSpace(raw) then
            []
        else
            let cleaned = Regex.Match(raw.Trim().TrimStart('v', 'V'), @"^\d+(\.\d+)*")

            if not cleaned.Success then
                []
            else
                cleaned.Value.Split('.')
                |> Array.map (fun p ->
                    match Int32.TryParse(p) with
                    | true, v -> v
                    | _ -> 0)
                |> Array.toList

    /// Positive when `a` is newer than `b`.
    let private compareVersions (a: string) (b: string) : int =
        let va = parseVersion a
        let vb = parseVersion b
        let length = max va.Length vb.Length

        let pad (list: int list) =
            list @ List.replicate (length - list.Length) 0

        compare (pad va) (pad vb)

    let private readRelease (element: JsonElement) : string * bool * string * string =
        let getString (name: string) : string =
            match element.TryGetProperty(name) with
            | true, (v: JsonElement) when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""

        let getBool (name: string) : bool =
            match element.TryGetProperty(name) with
            | true, (v: JsonElement) -> v.ValueKind = JsonValueKind.True
            | _ -> false

        let tag =
            let t = getString "tag_name"
            if String.IsNullOrWhiteSpace(t) then getString "name" else t

        let installerUrl, installerSha256 =
            match element.TryGetProperty("assets") with
            | true, (assets: JsonElement) when assets.ValueKind = JsonValueKind.Array ->
                assets.EnumerateArray()
                |> Seq.tryPick (fun asset ->
                    match asset.TryGetProperty("name"), asset.TryGetProperty("browser_download_url"), asset.TryGetProperty("digest") with
                    | (true, (name: JsonElement)), (true, (url: JsonElement)), (true, (digest: JsonElement))
                        when name.ValueKind = JsonValueKind.String
                             && url.ValueKind = JsonValueKind.String
                             && digest.ValueKind = JsonValueKind.String
                             && Regex.IsMatch(name.GetString(), @"^DLSS\.5\.MANAGER\..+\.exe$", RegexOptions.IgnoreCase)
                             && Regex.IsMatch(digest.GetString(), @"^sha256:[a-fA-F0-9]{64}$") ->
                        Some(url.GetString(), digest.GetString().Substring("sha256:".Length))
                    | _ -> None)
                |> Option.defaultValue ("", "")
            | _ -> "", ""

        (tag, getBool "draft" || getBool "prerelease", installerUrl, installerSha256)

    /// Newest published (non-draft, non-prerelease) release and its installer.
    let private fetchLatestRelease () : Async<string * string * string> =
        async {
            let! json = client.GetStringAsync(ReleasesApiUrl) |> Async.AwaitTask
            use document = JsonDocument.Parse(json)
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Array then
                return "", "", ""
            else
                let mutable best = ""
                let mutable bestInstallerUrl = ""
                let mutable bestInstallerSha256 = ""

                for item in root.EnumerateArray() do
                    let (tag, isPreview, installerUrl, installerSha256) = readRelease item

                    if not isPreview && not (String.IsNullOrWhiteSpace(tag)) then
                        if best = "" || compareVersions tag best > 0 then
                            best <- tag
                            bestInstallerUrl <- installerUrl
                            bestInstallerSha256 <- installerSha256

                return best, bestInstallerUrl, bestInstallerSha256
        }

    let check () : Async<UpdateResult> =
        async {
            try
                let! latest, installerUrl, installerSha256 = fetchLatestRelease ()

                if String.IsNullOrWhiteSpace(latest) then
                    return
                        { HasUpdate = false
                          LatestVersion = CurrentVersion
                          InstallerUrl = ""
                          InstallerSha256 = ""
                          Message = sprintf "You are on the latest version (v%s)." CurrentVersion }
                elif compareVersions latest CurrentVersion > 0 then
                    return
                        { HasUpdate = true
                          LatestVersion = latest.TrimStart('v', 'V')
                          InstallerUrl = installerUrl
                          InstallerSha256 = installerSha256
                          Message = sprintf "Version %s is available." (latest.TrimStart('v', 'V')) }
                else
                    return
                        { HasUpdate = false
                          LatestVersion = CurrentVersion
                          InstallerUrl = ""
                          InstallerSha256 = ""
                          Message = sprintf "You are on the latest version (v%s)." CurrentVersion }
            with ex ->
                return
                    { HasUpdate = false
                      LatestVersion = CurrentVersion
                      InstallerUrl = ""
                      InstallerSha256 = ""
                      Message = "Could not reach the update server. " + ex.Message }
        }

    let private quotePowerShell (value: string) =
        "'" + value.Replace("'", "''") + "'"

    /// Downloads the official release installer and starts a hidden Windows
    /// helper that waits for setup to finish, cleans up the download, and
    /// relaunches the app when setup exits.
    let downloadAndLaunchInstaller (installerUrl: string) (expectedSha256: string) (reportProgress: int64 -> int64 -> unit) : Async<unit> =
        async {
            let uri = Uri(installerUrl, UriKind.Absolute)

            if uri.Scheme <> Uri.UriSchemeHttps
               || not (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
               || not (uri.AbsolutePath.StartsWith("/NODIX-TECH/DLSS-5-MANAGER/releases/download/", StringComparison.OrdinalIgnoreCase)) then
                invalidArg (nameof installerUrl) "The release installer URL is not from the official GitHub repository."

            if not (Regex.IsMatch(expectedSha256, @"^[a-fA-F0-9]{64}$")) then
               invalidArg (nameof expectedSha256) "The release installer does not have a valid SHA-256 digest."

            let installerPath =
                Path.Combine(Path.GetTempPath(), "DLSS5Manager-update-" + Guid.NewGuid().ToString("N") + ".exe")

            try
                use! response =
                    downloadClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                    |> Async.AwaitTask

                response.EnsureSuccessStatusCode() |> ignore
                let expectedLength = response.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L
                use! input = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                use output = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)
                let buffer = Array.zeroCreate<byte> 81920
                use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                let progressTimer = Stopwatch.StartNew()
                let mutable received = 0L
                let mutable keepReading = true

                while keepReading do
                    let! count = input.ReadAsync(buffer, 0, buffer.Length) |> Async.AwaitTask

                    if count = 0 then
                        keepReading <- false
                    else
                        do! output.WriteAsync(buffer, 0, count) |> Async.AwaitTask
                        hash.AppendData(buffer, 0, count)
                        received <- received + int64 count

                        if progressTimer.ElapsedMilliseconds >= 500L then
                            reportProgress received expectedLength
                            progressTimer.Restart()

                do! output.FlushAsync() |> Async.AwaitTask

                if received = 0L || (expectedLength > 0L && received <> expectedLength) then
                    invalidOp "The downloaded release installer was incomplete."

                let actualSha256 = Convert.ToHexString(hash.GetHashAndReset())

                if not (actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) then
                    invalidOp "The downloaded release installer failed its GitHub SHA-256 integrity check."

                reportProgress received expectedLength

                let appPath = Environment.ProcessPath

                if String.IsNullOrWhiteSpace(appPath) then
                    invalidOp "Could not determine the running application path."

                let command =
                    sprintf
                        "$installer = %s; $app = %s; $directory = [IO.Path]::GetDirectoryName($app); $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"' + $directory + '\"'; try { Start-Process -FilePath $installer -ArgumentList $arguments -PassThru -Wait } finally { try { Remove-Item -LiteralPath $installer -Force } finally { Start-Process -FilePath $app } }"
                        (quotePowerShell installerPath)
                        (quotePowerShell appPath)

                let startInfo = ProcessStartInfo("powershell.exe")
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true
                startInfo.WindowStyle <- ProcessWindowStyle.Hidden
                startInfo.ArgumentList.Add("-NoProfile")
                startInfo.ArgumentList.Add("-WindowStyle")
                startInfo.ArgumentList.Add("Hidden")
                startInfo.ArgumentList.Add("-Command")
                startInfo.ArgumentList.Add(command)

                let helper = Process.Start(startInfo)

                if isNull helper then
                    invalidOp "Could not start the update installer."

                helper.Dispose()
            with ex ->
                if File.Exists(installerPath) then
                    try
                        File.Delete(installerPath)
                    with cleanupError ->
                        raise (AggregateException("The update failed and its temporary installer could not be removed.", ex, cleanupError))

                return raise ex
        }
