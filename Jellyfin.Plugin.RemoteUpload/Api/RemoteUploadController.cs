using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RemoteUpload.Api;

[ApiController]
[Authorize]
[Route("mediaupload")]
public class UploadController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static ConcurrentDictionary<string, (CancellationTokenSource cts, string filePath, string fileName, long fileSize)> _uploadTasks = new ConcurrentDictionary<string, (CancellationTokenSource, string, string, long)>();

    public UploadController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

    [HttpGet("user")]
    [AllowAnonymous]
    [Produces("text/html")]
    public IActionResult UserUploadPage()
    {
        const string html = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Remote Upload</title>
    <style>
        body { font-family: sans-serif; max-width: 680px; margin: 2rem auto; padding: 0 1rem; }
        .row { margin: 1rem 0; }
        button { padding: .65rem 1rem; cursor: pointer; }
        #status { margin-top: 1rem; white-space: pre-line; }
    </style>
</head>
<body>
    <h1>Remote Upload</h1>
    <p>Select files and upload directly to the server.</p>
    <p>Authentication token is detected automatically when possible. If upload fails with 401, paste token/API key below.</p>
    <div class="row">
        <label for="token">Token / API key (optional)</label><br />
        <input id="token" type="text" style="width:100%;padding:.5rem;" placeholder="Paste MediaBrowser token or api_key" />
    </div>
    <div class="row">
        <input id="files" type="file" multiple />
    </div>
    <div class="row">
        <button id="upload">Upload</button>
    </div>
    <div id="status"></div>

    <script>
        const filesInput = document.getElementById('files');
        const tokenInput = document.getElementById('token');
        const uploadBtn = document.getElementById('upload');
        const status = document.getElementById('status');

        function tryGetTokenFromCredentialStore(raw) {
            try {
                if (!raw) return null;
                const parsed = JSON.parse(raw);
                if (!parsed || !Array.isArray(parsed.Servers)) return null;

                const host = window.location.host.toLowerCase();
                const sameHostServer = parsed.Servers.find(s =>
                    s && typeof s.Address === 'string' && s.Address.toLowerCase().includes(host) && s.AccessToken);

                if (sameHostServer && sameHostServer.AccessToken) {
                    return sameHostServer.AccessToken;
                }

                const firstWithToken = parsed.Servers.find(s => s && s.AccessToken);
                return firstWithToken ? firstWithToken.AccessToken : null;
            } catch {
                return null;
            }
        }

        function getToken() {
            const manual = tokenInput.value.trim();
            if (manual) {
                return manual;
            }

            if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
                const token = window.ApiClient.accessToken();
                if (token) {
                    return token;
                }
            }

            const tokenFromQuery = new URLSearchParams(window.location.search).get('api_key');
            if (tokenFromQuery) {
                return tokenFromQuery;
            }

            const tokenFromQueryAlt = new URLSearchParams(window.location.search).get('token');
            if (tokenFromQueryAlt) {
                return tokenFromQueryAlt;
            }

            const credentialKeys = ['jellyfin_credentials', 'emby_credentials', 'credentials'];
            for (const key of credentialKeys) {
                const token = tryGetTokenFromCredentialStore(localStorage.getItem(key));
                if (token) {
                    tokenInput.value = token;
                    return token;
                }
            }

            return null;
        }

        uploadBtn.addEventListener('click', async () => {
            const files = filesInput.files;
            if (!files || files.length === 0) {
                status.textContent = 'No files selected.';
                return;
            }

            uploadBtn.disabled = true;
            status.textContent = 'Uploading...';

            const token = getToken();

            try {
                for (const file of files) {
                    const form = new FormData();
                    form.append('file', file);
                    form.append('chunkIndex', '0');
                    form.append('totalChunks', '1');

                    const headers = {};
                    let uploadUrl = '/mediaupload/upload';

                    if (token) {
                        headers.Authorization = 'MediaBrowser Token="' + token + '"';
                        headers['X-Emby-Token'] = token;
                        uploadUrl += '?api_key=' + encodeURIComponent(token);
                    }

                    const res = await fetch(uploadUrl, {
                        method: 'POST',
                        headers,
                        body: form,
                        credentials: 'include'
                    });

                    if (!res.ok) {
                        const bodyText = await res.text().catch(() => '');
                        let details = bodyText;
                        try {
                            const parsed = JSON.parse(bodyText);
                            details = parsed.message || bodyText;
                        } catch {
                            // Keep plain text details
                        }
                        throw new Error('Upload failed for ' + file.name + ' (HTTP ' + res.status + '). ' + (details || 'No error details.'));
                    }
                }

                status.textContent = 'Upload finished.';
                filesInput.value = '';
            } catch (err) {
                status.textContent = err.message || 'Upload failed.';
            } finally {
                uploadBtn.disabled = false;
            }
        });
    </script>
</body>
</html>
""";

        return Content(html, "text/html");
    }

    [HttpPost("upload")]
    public async Task<IActionResult> OnPostUploadAsync([FromForm] IFormFile file, [FromForm] int chunkIndex, [FromForm] int totalChunks)
    {
        try
        {
            PluginConfiguration? config = Plugin.Instance.Configuration;
            string uploaddir = config.uploaddir;
            var safeFileName = Path.GetFileName(file.FileName);

            if (!Directory.Exists(uploaddir))
            {
                Directory.CreateDirectory(uploaddir);
            }

            if (file.Length > 0) {
                var tempFilePath = Path.Combine(uploaddir, $"{safeFileName}.part");

                using (var stream = new FileStream(tempFilePath, chunkIndex == 0 ? FileMode.Create : FileMode.Append))
                {
                    await file.CopyToAsync(stream);
                }

                if (chunkIndex + 1 == totalChunks)
                {
                    // All chunks uploaded, rename the temporary file to the original filename
                    var finalFilePath = Path.Combine(uploaddir, safeFileName);
                    if (System.IO.File.Exists(finalFilePath))
                    {
                        System.IO.File.Delete(finalFilePath);
                    }
                    System.IO.File.Move(tempFilePath, finalFilePath);
                }
            }

            return Ok(new { name = safeFileName, chunk = chunkIndex });
        }
        catch (Exception ex) // Catch any other exceptions
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("upload_url")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> URLOnPostUploadAsync([FromForm] string url) {
        if (string.IsNullOrEmpty(url))
        {
            return BadRequest(new { message = "URL is required" });
        }

        PluginConfiguration? config = Plugin.Instance.Configuration;
        string uploaddir = config.uploaddir;

        if (!Directory.Exists(uploaddir))
        {
            Directory.CreateDirectory(uploaddir);
        }

        if (!IsDirectoryWritable(uploaddir)) {
            return BadRequest(new { message = "No permission to write in directory" });
        }

        string cancellationKey = Guid.NewGuid().ToString();
        var cts = new CancellationTokenSource();

        string? filename = null;
        string? destinationPath = null;
        long filesize = 0;

        var task = Task.Run(async () => 
        {
            try
            {
                using (var client = _httpClientFactory.CreateClient())
                {
                    using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        response.EnsureSuccessStatusCode(); 

                        filename = GetFileName(response, url);
                        filesize = GetFileSize(response);

                        destinationPath = Path.Combine(uploaddir, filename);

                        _uploadTasks.TryAdd(cancellationKey, (cts, destinationPath, filename, filesize)); // Add this task to uploadTasks

                        using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                        using (FileStream fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await contentStream.CopyToAsync(fileStream, cts.Token);
                        }
                        
                    }
                }
            }
            catch (Exception)
            { }
            finally {
                _uploadTasks.TryRemove(cancellationKey, out _); // remove the task from uploadTasks when download is finished
            }
        }, cts.Token);

        await Task.Delay(500); // Wait until download starts

        if (!_uploadTasks.ContainsKey(cancellationKey)) // If download has started, there should be a cancellation key, wait 3 seconds
        {
            await Task.Delay(1000);
            if (!_uploadTasks.ContainsKey(cancellationKey)) // If download has started, there should be a cancellation key
            {
                return BadRequest(new { message = "Download link not working" });
            }
        }

        return Ok(new { message = "Success" });
    }

    [HttpPost("upload_bulk_url")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> URLOnPostBulkUploadAsync([FromForm] List<string> urls) {
        for (int i = 0; i<urls.Count; i++) {
            if (string.IsNullOrEmpty(urls[i]))
            {
                return BadRequest(new { message = "URL is required" });
            }
        }

        PluginConfiguration? config = Plugin.Instance.Configuration;
        string uploaddir = config.uploaddir;

        if (!Directory.Exists(uploaddir))
        {
            Directory.CreateDirectory(uploaddir);
        }

        if (!IsDirectoryWritable(uploaddir)) {
            return BadRequest(new { message = "No permission to write in directory" });
        }

        string cancellationKey = "";
        var cts = new CancellationTokenSource();

        string? filename = null;
        string? destinationPath = null;
        string ex = "";
        long filesize = 0;
        bool started = false;

        var task = Task.Run(async () => 
        {
            foreach (var url in urls) {
                cancellationKey = Guid.NewGuid().ToString();
                try
                {
                    using (var client = _httpClientFactory.CreateClient())
                    {
                        using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        {
                            response.EnsureSuccessStatusCode(); 

                            filename = GetFileName(response, url);
                            filesize = GetFileSize(response);

                            destinationPath = Path.Combine(uploaddir, filename);

                            _uploadTasks.TryAdd(cancellationKey, (cts, destinationPath, filename, filesize)); // Add this task to uploadTasks
                            started=true;

                            using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                            using (FileStream fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await contentStream.CopyToAsync(fileStream, cts.Token);
                            }
                            
                        }
                    }
                }
                catch (Exception e)
                { 
                    ex=e.Message;
                }
                finally {
                    _uploadTasks.TryRemove(cancellationKey, out _); // remove the task from uploadTasks when download is finished
                }
            }
        }, cts.Token);

        await Task.Delay(2000);
        if (!started) {
            return BadRequest(new { message = ex });
        }

        return Ok(new { message = "Success" });
    }

    [HttpPost]
    [Route("download")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> DownloadFile([FromForm] string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return BadRequest(new { message = "File not found!" });
        }

        var memory = new MemoryStream();
        using (var stream = new FileStream(path, FileMode.Open))
        {
            await stream.CopyToAsync(memory);
        }
        memory.Position = 0;

        var contentType = "application/octet-stream";
        var fileName = Path.GetFileName(path);

        return File(memory, contentType, fileName);
    }

    [HttpPost]
    [Route("directory")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult ListFolderContent([FromForm] string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return BadRequest(new { message = "Folder not found!" });
            }

            List<string> files = new List<string>();

            foreach (string folder in Directory.GetDirectories(path)) {
                files.Add(Path.GetFileName(folder)+"/");
            }
            foreach (string file in Directory.GetFiles(path)) {
                files.Add(Path.GetFileName(file));
            }

            return Ok(files);
        }
        catch (Exception ex)
        {
            // Log the exception details
            Console.WriteLine($"Error: {ex.Message}");
            return StatusCode(500, new { message = "Internal server error." });
        }
    }

    private string GetFileName(HttpResponseMessage response, string url) {
        string? filename = null;

        if (response.Content.Headers.ContentDisposition != null) {
            var contentDisposition = response.Content.Headers.ContentDisposition;
            if (!string.IsNullOrEmpty(contentDisposition.FileName)) {
                filename = contentDisposition.FileName.Trim('\"');
            }
            if (!string.IsNullOrEmpty(contentDisposition.FileNameStar))
            {
                filename = contentDisposition.FileNameStar.Trim('\"');
            }
        }

        if (filename == null) {
            Uri uri = new Uri(url);
            filename = Path.GetFileName(uri.AbsolutePath);
        }

        return filename ?? "filewithoutname.mp4";
    }

    private long GetFileSize(HttpResponseMessage response) {
        try {
            if (response.Content.Headers.TryGetValues("Content-Length", out var values))
            {
                var contentLength = values.FirstOrDefault();
                if (long.TryParse(contentLength, out var fileSize))
                {
                    // We have a fileSize in bytes
                    return fileSize;
                }
                else
                {
                    // fileSize is not a long, we return 0
                    return 0;
                }
            }
            else
            {
                // No filesize in headers
                return 0;
            }
        }
        catch (Exception) {
            // Some error, we return 0
            return 0;
        }
    }

    private bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
    {
        try
        {
            using (FileStream fs = new FileStream(Path.Combine(dirPath, Path.GetRandomFileName()), FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            { }
            return true;
        }
        catch
        {
            if (throwIfFails)
                throw;
            else
                return false;
        }
    }

    // Cancel a URL download
    [HttpPost("upload_cancel")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> CancelUpload([FromForm] string cancellationKey)
    {
        try {
            if (_uploadTasks.TryRemove(cancellationKey, out var taskInfo))
            {
                // This cancels the task, the file will also be deleted as in task.ContinueWith
                taskInfo.cts.Cancel();

                await Task.Delay(3000); // Wait three seconds to make sure, that the task is finished
                if (System.IO.File.Exists(taskInfo.filePath)) {
                    System.IO.File.Delete(taskInfo.filePath);
                }

                return Ok(new { message = "Upload canceled", filename = taskInfo.fileName});
            }
            else
            {
                return BadRequest(new { message = "Task doesn't exist" });
            }
        }
        catch (Exception ex) {
            return BadRequest(new { message = ex.Message });
        }
        
    }

    // Gets all running tasks
    [HttpGet("get_tasks")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetUploadTasks()
    {
        var tasks = _uploadTasks.Select(task => new
        { 
            Key = task.Key,
            FileName = task.Value.fileName, 
            FileSize = task.Value.fileSize, 
            FileSizeNow = new FileInfo(task.Value.filePath).Length
        });

        return Ok(tasks);
    }
}
