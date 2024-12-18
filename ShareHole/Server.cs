﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Net.Mime;
using HeyRed.Mime;
using ShareHole.Configuration;
using System.ComponentModel.Design;
using System.Drawing;
using ShareHole.DBThreads;
using ImageMagick;
using System.Net.Http.Headers;
using FFMpegCore;
using System.Security.Cryptography;
using ShareHole.Threads;

namespace ShareHole
{
    public class FolderServer {
        bool running = true;
        HttpListener listener;
        
        string CSS = "";
        public string id { get; private set; }
        public string name { get; private set; }

        string base_page_content = "";

        string page_content_strings_replaced(string page_content, string page_title) {
             return base_page_content.Replace("{page_content}", page_content).Replace("{page_title}", page_title);
        }

        string base_css_data_replaced {
            get { return CurrentConfig.base_css.Replace("{thumbnail_size}", CurrentConfig.server["gallery"]["thumbnail_size"].ToInt().ToString()); }
        }

        int dispatch_thread_count = 64;
        public Thread[] dispatch_threads;

        public void StartServer(string id) {
            this.id = id;

            if (CurrentConfig.use_html_file) {
                if (File.Exists("base.html"))
                    base_page_content = File.ReadAllText("base.html");
                else {
                    Logging.Error("use_css_file enabled, but base.css is missing from the config directory. Writing default.");
                    base_page_content = CurrentConfig.base_html;
                    File.WriteAllText("base.html", base_page_content);
                }
            } else {
                base_page_content = CurrentConfig.base_html;
            }

            if (CurrentConfig.use_css_file) {
                if (File.Exists("base.css")) {
                    CSS = File.ReadAllText("base.css");
                } else {
                    Logging.Error("use_css_file enabled, but base.css is missing from the config directory. Writing default.");
                    CSS = base_css_data_replaced;
                    File.WriteAllText("base.css", CSS);
                }
            } else {
                CSS = base_css_data_replaced;
            }

            listener = new HttpListener();

            var port = CurrentConfig.server["server"]["port"].ToInt();
            var prefixes = CurrentConfig.server["server"]["prefix"].ToString().Trim().Split(' ');
            dispatch_thread_count = CurrentConfig.server["server"]["threads"].ToInt();

            var p = prefixes[0];
            if (p.StartsWith("http://")) p = p.Remove(0, 7);
            if (p.StartsWith("https://")) p = p.Remove(0, 8);
            if (p.EndsWith('/')) p = p.Remove(p.Length - 1, 1);
            name = $"{p}:{port}";

            for (int i = 0; i < prefixes.Length; i++) {
                string prefix = prefixes[i].Trim();

                if (prefix.StartsWith("http://")) prefix = prefix.Remove(0, 7);
                if (prefix.StartsWith("https://")) prefix = prefix.Remove(0, 8);
                if (prefix.EndsWith('/')) prefix = prefix.Remove(prefix.Length - 1, 1);

                listener.Prefixes.Add($"http://{prefix}:{port}/");
                Logging.Message("Using prefix: " + $"http://{prefix}:{port}/");
            }

            listener.Start();

            dispatch_threads = new Thread[dispatch_thread_count];

            Logging.Message($"Starting server on port {port}");

            for (int i = 0; i < dispatch_thread_count; i++) {
                dispatch_threads[i] = new Thread(RequestThread);
                dispatch_threads[i].Name = $"{prefixes[0]}:{port}:{i}";
                dispatch_threads[i].Start((dispatch_threads[i].Name, i));
            }
        }

        public void StopServer() {
            running = false;

            CurrentConfig.cancellation_token_source.Cancel(true);

            while (true) {
                if (all_threads_stopped())
                    break;
            }

            Logging.Message($"All threads stopped");

            listener.Stop();
        }

        bool all_threads_stopped () {
            int i = 0;

            foreach(Thread t in dispatch_threads) {
                if (t.ThreadState != ThreadState.Stopped) {
                    i++;
                }
            }

            return i == 0;
        }

        async void RequestThread(object? name_id) {
            (string name, int id) nid = (((string, int))name_id);
            string thread_name = nid.name.ToString();
            int thread_id = nid.id;
            Logging.ThreadMessage($"Started thread", thread_name, thread_id);

            while (listener.IsListening && running) {
                HttpListenerContext context = null;
                try {
                    //Asynchronously begin waiting for a new HTTP request,
                    //but continue on to the while loop below to make it
                    //possible to exit idly waiting threads 
                    listener.GetContextAsync().ContinueWith(a => {
                        context = a.Result;
                    }, CurrentConfig.cancellation_token);

                    //Wait for a new HTTP request
                    while (context == null) {
                        if (CurrentConfig.cancellation_token.IsCancellationRequested) {
                            Logging.ThreadMessage($"Stopping thread", thread_name, thread_id);
                            return;
                        }

                        //sleep for a random amount of time, purely to reduce cpu usage at idle. 
                        Thread.Sleep(Random.Shared.Next(5, 50));
                    }

                } catch (HttpListenerException ex) {
                    //if we're not running, then that means Stop was called, so this error is expected, same with the ObjectDisposedException                    
                    if (running) {
                        Logging.ThreadError($"Failed to get context: {ex.Message}", thread_name, thread_id);
                    }
                    continue;

                } catch (ObjectDisposedException ex) {
                    if (running) {
                        Logging.ThreadError($"Failed to get context: {ex.Message}", thread_name, thread_id);
                    }
                    continue;
                }

                var request = context.Request;

                //Set up response
                context.Response.KeepAlive = false;
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.AddHeader("X-Frame-Options", "SAMEORIGIN");
                context.Response.AddHeader("Keep-alive", "false");
                context.Response.AddHeader("Cache-control", "no-cache");
                context.Response.AddHeader("Content-Disposition", "inline");
                context.Response.AddHeader("Accept-ranges", "bytes");
                context.Response.SendChunked = true;

                //only support GET
                if (context.Request.HttpMethod != "GET") {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    continue;
                }

                //No current favicon support
                if (request.Url.AbsolutePath == "/favicon.ico") {
                    context.Response.Abort();
                    continue;
                }

                string page_content = "";

                string url_path = Uri.UnescapeDataString(request.Url.AbsolutePath);
                string passdir = CurrentConfig.server["server"]["passdir"].ToString().Trim();

                string share_name = "";
                string folder_path = "";

                bool thumbnail = false;
                bool to_jpg = false;
                bool to_png = false;
                bool transcode = false;
                bool stream = false;
                bool file_list = false;
                bool music_player = false;

                //Check if passdir is correct
                if (!url_path.StartsWith($"/{passdir}/") || url_path == ($"/{passdir}/")) {
                    context.Response.Abort();
                    continue;
                } else {
                    url_path = url_path.Remove(0, passdir.Length + 1);
                }

                //check for command directory
                if (url_path.ToLower().StartsWith("/thumbnail/")) {
                    url_path = url_path.Remove(0, "/thumbnail/".Length);
                    thumbnail = true;
                } else if (url_path.ToLower().StartsWith("/to_jpg/")) {
                    url_path = url_path.Remove(0, "/to_jpg/".Length);
                    to_jpg = true;
                } else if (url_path.ToLower().StartsWith("/to_png/")) {
                    url_path = url_path.Remove(0, "/to_png/".Length);
                    to_png = true;
                } else if (url_path.ToLower().StartsWith("/transcode/")) {
                    url_path = url_path.Remove(0, "/transcode/".Length);
                    transcode = true;
                } else if (url_path.ToLower().StartsWith("/file_list/")) {
                    url_path = url_path.Remove(0, "/file_list/".Length);
                    file_list = true;
                } else if (url_path.ToLower().StartsWith("/music_player/")) {
                    url_path = url_path.Remove(0, "/music_player/".Length);
                    music_player = true;
                }

                //Clean URL
                while (url_path.StartsWith('/')) {
                    url_path = url_path.Remove(0, 1);
                }

                //Extract share name from start of URL
                var slash_i = url_path.IndexOf('/');
                if (slash_i > 0) {
                    share_name = url_path.Substring(0, slash_i);
                    if (share_name.EndsWith('/')) share_name = share_name.Remove(share_name.Length - 1, 1);

                } else if (!request.Url.AbsolutePath.EndsWith("base.css")) {
                    //if the user types, for example, localhost:8080/loot/share instead of /loot/share/
                    //redirect to /loot/share/ so that the rest of this garbage works
                    share_name = url_path;
                    url_path += "/";
                    context.Response.Redirect(url_path);
                    Logging.ThreadWarning($"Share recognized, missing trailing slash, redirecting to {url_path}", thread_name, thread_id);
                }

                bool show_dirs = true;

                //if requested share exists
                if (CurrentConfig.shares.ContainsKey(share_name) && !request.Url.AbsolutePath.EndsWith("base.css")) {
                    //Check if directories should be listed
                    if (CurrentConfig.shares[share_name].ContainsKey("show_directories")) {
                        show_dirs = CurrentConfig.shares[share_name]["show_directories"].ToBool();
                    }

                    folder_path = CurrentConfig.shares[share_name]["path"].ToString();
                    //Logging.Message($"Accessing share: {share_name}");

                } else if (!request.Url.AbsolutePath.EndsWith("base.css")) {
                    Logging.ThreadError($"Client requested share which doesn't exist: {share_name} {url_path}", thread_name, thread_id);
                    context.Response.Abort();
                    continue;
                }
                url_path = url_path.Remove(0, share_name.Length);

                string absolute_on_disk_path = folder_path.Replace("\\", "/") + Uri.UnescapeDataString(url_path);

                var ext = new FileInfo(absolute_on_disk_path).Extension.Replace(".", "");
                var mime = Conversion.GetMimeTypeOrOctet(absolute_on_disk_path);

                //Requested thumbnail
                if (thumbnail && File.Exists(absolute_on_disk_path)) {
                    if (mime.StartsWith("video") || Conversion.IsValidImage(mime)) {
                        enable_cache(context);
                        ThumbnailManager.RequestThumbnail(absolute_on_disk_path, context, this, mime, thread_id);

                    } else {
                        page_content = $"<p class=\"head\"><color=white><b>NOT AN IMAGE, VIDEO OR POSTSCRIPT FILE</b></p>";
                        error_bad_request(page_content, context);
                    }

                    //Requested RAW to JPG
                } else if (to_jpg && File.Exists(absolute_on_disk_path)) {
                    if (Conversion.IsValidImage(mime)) {
                        enable_cache(context);
                        using (MagickImage mi = new MagickImage(absolute_on_disk_path)) {
                            if (mi.Orientation != OrientationType.Undefined)
                                mi.AutoOrient();

                            mi.Settings.Format = MagickFormat.Jpg;

                            var compress = CurrentConfig.server["conversion"]["jpeg_compression"].ToBool();
                            var quality = CurrentConfig.server["conversion"]["jpeg_quality"].ToInt();

                            if (quality < 0) quality = 0;
                            if (quality > 100) quality = 100;

                            if (compress) {
                                mi.Settings.Compression = CompressionMethod.JPEG;
                                mi.Quality = (uint)quality;
                            } else {
                                mi.Settings.Compression = CompressionMethod.LosslessJPEG;
                                mi.Quality = 100;
                            }

                            context.Response.ContentType = "image/jpeg";

                            var bytes = mi.ToByteArray();
                            using (MemoryStream ms = new MemoryStream(bytes, false)) {
                                var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                                    context.Response.StatusDescription = "200 OK";
                                    context.Response.Close();
                                }, CurrentConfig.cancellation_token);
                            }
                        }

                    } else {
                        page_content = $"<p class=\"head\"><color=white><b>NOT AN IMAGE FILE</b></p>";
                        error_bad_request(page_content, context);
                    }

                    //Requested image to PNG
                } else if (to_png && File.Exists(absolute_on_disk_path)) {
                    if (Conversion.IsValidImage(mime)) {
                        enable_cache(context);
                        MagickReadSettings settings = null;

                        var vector = mime == "application/pdf" || mime == "application/postscript";

                        if (vector) {
                            settings = new MagickReadSettings {
                                Density = new Density(300)
                            };
                        }

                        using (MagickImage mi = new MagickImage(absolute_on_disk_path, settings)) {

                            if (mi.Orientation != OrientationType.Undefined)
                                mi.AutoOrient();


                            mi.Settings.Format = MagickFormat.Png;

                            //if (pdf) mi.Resize(new Percentage(300), new Percentage(300));

                            context.Response.ContentType = "image/png";

                            var bytes = mi.ToByteArray();
                            using (MemoryStream ms = new MemoryStream(bytes, false)) {
                                var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                                    context.Response.StatusDescription = "200 OK";
                                    context.Response.Close();
                                }, CurrentConfig.cancellation_token);
                            }
                        }

                    } else {
                        page_content = $"<p class=\"head\"><color=white><b>NOT AN IMAGE FILE</b></p>";
                        error_bad_request(page_content, context);
                    }

                    //Transcode videos and audio
                } else if (transcode && File.Exists(absolute_on_disk_path)) {
                    if (mime.StartsWith("video")) {
                        Conversion.Video.transcode_mp4_full(new FileInfo(absolute_on_disk_path), context);

                    } else if (mime.StartsWith("audio")) {

                    } else {
                        page_content = $"<p class=\"head\"><color=white><b>NOT A VIDEO FILE</b></p>";
                        error_bad_request(page_content, context);
                    }
                    //Requested CSS file  
                } else if (request.Url.AbsolutePath.EndsWith("base.css")) {
                    absolute_on_disk_path = Path.GetFullPath("base.css");
                    Logging.ThreadMessage($"Requested base.css", thread_name, thread_id);

                    var data = Encoding.UTF8.GetBytes(CSS);
                    context.Response.ContentType = "text/css; charset=utf-8";
                    context.Response.ContentLength64 = data.LongLength;

                    //enable_cache(context);

                    using (MemoryStream ms = new MemoryStream(data, false)) {
                        var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                            context.Response.StatusCode = (int)HttpStatusCode.OK;
                            context.Response.StatusDescription = "200 OK";
                            context.Response.Close();
                        }, CurrentConfig.cancellation_token);
                    }

                    //Requested file list
                } else if (file_list && Directory.Exists(absolute_on_disk_path)) {
                    var di = new DirectoryInfo(absolute_on_disk_path);
                    enable_cache(context);

                    var files = di.GetFiles();

                    string raw_file_list = "";

                    foreach (FileInfo fi in files) {
                        raw_file_list += $"http://{request.UserHostName}/{passdir}/{share_name}{url_path}/{fi.Name}\n";
                    }

                    var data = Encoding.UTF8.GetBytes(raw_file_list);
                    try {
                        using (MemoryStream ms = new MemoryStream(data, false)) {
                            var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                                context.Response.StatusCode = (int)HttpStatusCode.OK;
                                context.Response.StatusDescription = "200 OK";
                                context.Response.Close();

                                Logging.ThreadMessage($"Sent directory listing for {url_path}", thread_name, thread_id);
                            }, CurrentConfig.cancellation_token);
                        }
                    } catch (HttpListenerException ex) {
                        Logging.ThreadError($"Exception: {ex.Message}", thread_name, thread_id);
                    }

                    //Requested music player
                } else if (music_player && Directory.Exists(absolute_on_disk_path)) {

                    var di = new DirectoryInfo(absolute_on_disk_path);

                    var files = di.GetFiles();

                    string raw_file_list = "";
                    foreach (FileInfo fi in files) {
                        if (!Conversion.GetMimeTypeOrOctet(fi.Name).StartsWith("audio")) continue;
                        raw_file_list += $"<li onclick=\"loadSong('http://{request.UserHostName}/{passdir}/{share_name}{Uri.EscapeDataString(url_path + fi.Name)}')\">{fi.Name}</li>\n";
                    }

                    string file_list_array = "['";
                    int c = 0;
                    if (files.Length > 0) {

                        foreach (FileInfo fi in files) {
                            if (!Conversion.GetMimeTypeOrOctet(fi.Name).StartsWith("audio")) continue;

                            if (c > 0) file_list_array += $"', '";

                            file_list_array += $"http://{request.UserHostName}/{passdir}/{share_name}{Uri.EscapeDataString(url_path + fi.Name)}";
                            

                            c++;

                        }
                        file_list_array += "'];";

                    } else {
                        file_list_array = "[];";
                    }

                    string player = MusicPlayer.music_player_content;
                    player = player.Replace("{track_list}", raw_file_list).Replace("{file_array}", file_list_array)
                                   .Replace("{local_dir}", $"http://{request.UserHostName}/{passdir}/{share_name}{Uri.EscapeDataString(url_path)}");
                    context.Response.ContentType = "text/html; charset=utf-8";
                    
                    var data = Encoding.UTF8.GetBytes(player);
                    context.Response.ContentLength64 = data.LongLength;

                    try {
                        using (MemoryStream ms = new MemoryStream(data, false)) {
                            var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                                context.Response.StatusCode = (int)HttpStatusCode.OK;
                                context.Response.StatusDescription = "200 OK";
                                //context.Response.Close();

                                Logging.ThreadMessage($"Sent directory listing for {url_path}", thread_name, thread_id);
                            }, CurrentConfig.cancellation_token);
                        }
                    } catch (HttpListenerException ex) {
                        Logging.ThreadError($"Exception: {ex.Message}", thread_name, thread_id);
                    }


                    //Requested a directory
                } else if (Directory.Exists(absolute_on_disk_path)) {
                    byte[] data = null;

                    if (!show_dirs && url_path != "/") {
                        Logging.ThreadError($"Attempted to browse outside of share \"{share_name}\" with directories off", thread_name, thread_id);
                        page_content = "";
                        context.Response.Abort();
                        continue;
                    } else {
                        //Get the page content based on the share's chosen render style
                        if (CurrentConfig.shares[share_name].ContainsKey("style")) {
                            switch (CurrentConfig.shares[share_name]["style"].ToString()) {                                
                                case "gallery":
                                    page_content = Renderer.Gallery(folder_path, request.UserHostName, url_path, share_name);
                                    data = Encoding.UTF8.GetBytes(page_content_strings_replaced(page_content, ""));
                                    break;
                                case "music":
                                    page_content = Renderer.MusicPlayerContent(folder_path, request.UserHostName, url_path, share_name);
                                    data = Encoding.UTF8.GetBytes(page_content_strings_replaced(page_content, ""));
                                    break;
                                default:
                                    page_content = Renderer.FileListing(folder_path, request.UserHostName, url_path, share_name);
                                    data = Encoding.UTF8.GetBytes(page_content_strings_replaced(page_content, ""));
                                    break;
                            }
                        } else {
                            //There isn't a render style given in the config, so just use the regular list style
                            page_content = Renderer.FileListing(folder_path, request.UserHostName, url_path, share_name);
                            data = Encoding.UTF8.GetBytes(page_content_strings_replaced(page_content, ""));
                        }                    
                    }

                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = data.LongLength;

                    try {
                        using (MemoryStream ms = new MemoryStream(data, false)) {
                            var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                                context.Response.StatusCode = (int)HttpStatusCode.OK;
                                context.Response.StatusDescription = "200 OK";
                                context.Response.Close();

                                Logging.ThreadMessage($"Sent directory listing for {url_path}", thread_name, thread_id);
                            }, CurrentConfig.cancellation_token);
                        }

                    } catch (HttpListenerException ex) {
                        Logging.ThreadError($"Exception: {ex.Message}", thread_name, thread_id);
                    }

                    //Requested a file
                } else if (File.Exists(absolute_on_disk_path)) {
                    string mimetype = Conversion.GetMimeTypeOrOctet(absolute_on_disk_path);

                    try {
                        if (mimetype.StartsWith("video")) {
                            var anal = FFProbe.Analyse(absolute_on_disk_path);
                            context.Response.AddHeader("X-Content-Duration", ((int)(anal.Duration.TotalSeconds) + 1).ToString());
                        }
                    } catch (Exception ex) { }

                    if (!show_dirs && url_path.Count(x => x == '/') > 1) {
                        Logging.ThreadError($"Attempted to open file outside of share \"{share_name}\" with directories off", thread_name, thread_id);
                        context.Response.Abort();
                        continue;
                    }

                    var using_extensions = false;
                    string[] extensions = null;

                    if (CurrentConfig.shares[share_name].ContainsKey("extensions")) {
                        extensions = CurrentConfig.shares[share_name]["extensions"].ToString().Trim().ToLower().Split(" ");
                        using_extensions = true;
                        for (int i = 0; i < extensions.Length; i++) {
                            extensions[i] = extensions[i].Trim();
                            extensions[i] = extensions[i].Replace(".", "");
                        }
                    }

                    if (using_extensions && (Path.HasExtension(absolute_on_disk_path) && !extensions.Contains(Path.GetExtension(absolute_on_disk_path).Replace(".","").ToLower()))) {
                        Logging.ThreadError($"Attempted to open file in \"{share_name}\" with disallowed file extension \"{Path.GetExtension(absolute_on_disk_path).Replace(".", "").ToLower()}\"", thread_name, thread_id);
                        context.Response.Abort();
                        continue;
                    }

                    Logging.ThreadMessage($"[Share] {share_name} [Filename]: {absolute_on_disk_path} [Content-type] {mimetype}", thread_name, thread_id);

                    enable_cache(context);
                    context.Response.AddHeader("filename", request.Url.AbsolutePath.Remove(0, 1));
                    context.Response.ContentType = mimetype;                        
                    context.Response.SendChunked = false;

                    SendFile.SendWithRanges(absolute_on_disk_path, mimetype, context);

                    //User gave a very fail URL
                } else {
                    page_content = $"<b>NOT FOUND</b>";
                    error404(page_content, context);
                }
            }

            Logging.ThreadMessage($"Stopped thread", thread_name, thread_id);

        }

        void error404(string page_content, HttpListenerContext context) {
            var data = Encoding.UTF8.GetBytes(page_content_strings_replaced(page_content, ""));
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = data.LongLength;

            using (MemoryStream ms = new MemoryStream(data, false)) {
                var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.StatusDescription = "404 NOT FOUND";
                    context.Response.Close();
                }, CurrentConfig.cancellation_token);
            }

        }
        void error_bad_request(string page_content, HttpListenerContext context) {
            var data = Encoding.UTF8.GetBytes(page_content_strings_replaced(page_content, ""));
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = data.LongLength;

            using (MemoryStream ms = new MemoryStream(data, false)) {
                var task = ms.CopyToAsync(context.Response.OutputStream).ContinueWith(a => {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.StatusDescription = "400 BAD REQUEST";
                    context.Response.Close();
                }, CurrentConfig.cancellation_token);
            }

        }

        public static byte[] ImageToByte(Image img) {
            ImageConverter converter = new ImageConverter();
            return (byte[])converter.ConvertTo(img, typeof(byte[]));
        }

        void enable_cache(HttpListenerContext context) {
            context.Response.Headers.Remove("Cache-control");
            context.Response.AddHeader("Cache-control", "max-age=86400, public");
        }
    }
}