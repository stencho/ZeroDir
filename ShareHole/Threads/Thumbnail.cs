﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using ImageMagick;

namespace ShareHole.DBThreads {
    public class ThumbnailRequest {
        public FileInfo file;
        public byte[] thumbnail = null;

        public string mime_type = "";
        
        public bool thumbnail_ready = false;
        public bool thread_dispatched = false;

        public int thread_id = 0;

        public HttpListenerContext context;
        public HttpListenerResponse response => context.Response;
        public HttpListenerRequest request => context.Request;

        public FolderServer parent_server;
        
        public ThumbnailRequest(FileInfo file, HttpListenerContext context, FolderServer parent_server, string mime_type, int thread_id) {
            this.file = file;
            this.context = context;
            this.parent_server = parent_server;
            this.thread_id = thread_id;
            this.mime_type = mime_type;
        }
    }

    public static class ThumbnailManager {
        static int thumbnail_size = 192;

        //cache for thumbnails which have been loaded at least once
        static volatile Dictionary<string, (string mime, byte[] data)> thumbnail_cache = new Dictionary<string, (string mime, byte[] data)>();

        static int thumb_compression_quality => CurrentConfig.server["gallery"]["thumbnail_compression_quality"].ToInt();

        public static void RequestThumbnail(string filename, HttpListenerContext context, FolderServer parent_server, string mime_type, int thread_id) {
            FileInfo f = new FileInfo(filename);
            if (f.Exists) {
                RequestThumbnail(f, context, parent_server, mime_type, thread_id);
            } else return;
        }

        public static void RequestThumbnail(FileInfo file, HttpListenerContext context, FolderServer parent_server, string mime_type, int thread_id) {
            var tr = new ThumbnailRequest(file, context, parent_server, mime_type, thread_id);

            Task.Run(() => { build_thumbnail(tr); }, CurrentConfig.cancellation_token);

            //Task bt = new Task(build_thumbnail, CurrentConfig.cancellation_token);
        }

        static byte[] get_first_video_frame_from_ffmpeg(ThumbnailRequest request) {
            byte[] output;

            var anal = FFProbe.Analyse(request.file.FullName);

            double final_x, final_y;

            double aspect = (double)anal.PrimaryVideoStream.Width / (double)anal.PrimaryVideoStream.Height;

            if (aspect >= 0) {
                final_x = thumbnail_size * aspect;
                final_y = thumbnail_size;
            } else {
                final_y = thumbnail_size * aspect;
                final_x = thumbnail_size;
            }

            using (var stream_output = new MemoryStream()) {
                var stream_video = FFMpegArguments
                    .FromFileInput(request.file)
                    .OutputToPipe(new StreamPipeSink(stream_output), options =>
                        options.WithFrameOutputCount(1)
                        .WithVideoCodec(VideoCodec.Png)
                        .Resize((int)Math.Round(final_x), (int)Math.Round(final_y))
                        .ForceFormat("image2pipe")
                        )
                    .ProcessSynchronously();

                output = stream_output.ToArray();
            }

            return output;
        }

        static async void build_thumbnail(ThumbnailRequest request) {
            cache_fail:
            thumbnail_size = CurrentConfig.server["gallery"]["thumbnail_size"].ToInt();
            
            //cache hit, do nothing
            if (thumbnail_cache.ContainsKey(request.file.FullName)) {
                if (CurrentConfig.LogLevel == Logging.LogLevel.ALL)
                    Logging.ThreadMessage($"Cache hit for {request.file.Name}", $"THUMB:{request.thread_id}", request.thread_id);

                //build new thumbnail for an image and add it to the cache
            } else if (request.mime_type.StartsWith("image") || request.mime_type == "application/postscript" || request.mime_type == "application/pdf") {
                if (CurrentConfig.LogLevel == Logging.LogLevel.ALL)
                    Logging.ThreadMessage($"Building thumbnail for image {request.file.Name}", $"THUMB:{request.thread_id}", request.thread_id);

                MagickImage mi = new MagickImage(request.file.FullName);

                if (mi.Orientation != OrientationType.Undefined)
                    mi.AutoOrient();

                mi.Resize((uint)thumbnail_size, (uint)thumbnail_size);

                if (CurrentConfig.server["gallery"]["thumbnail_compression"].ToBool()) {
                    Conversion.Image.ConvertToJpeg(mi, (uint)thumb_compression_quality);

                    try {
                        lock (thumbnail_cache) thumbnail_cache.Add(request.file.FullName, ("image/jpeg", mi.ToByteArray()));
                    } catch (Exception ex) {
                        Logging.Error($"{request.file.Name} :: {ex.Message}");
                    }

                } else {
                    Conversion.Image.ConvertToPng(mi);

                    try {
                        lock (thumbnail_cache) thumbnail_cache.Add(request.file.FullName, ("image/png", mi.ToByteArray()));
                    } catch (Exception ex) {
                        Logging.Error($"{request.file.Name} :: {ex.Message}");
                    }
                }


                //build one for a video
            } else if (request.mime_type.StartsWith("video")) {
                if (CurrentConfig.LogLevel == Logging.LogLevel.ALL)
                    Logging.ThreadMessage($"Building thumbnail for video {request.file.Name}", $"THUMB:{request.thread_id}", request.thread_id);

                try {
                    var ffmpeg_thumb = get_first_video_frame_from_ffmpeg(request);

                    byte[] img_data;

                    using (MemoryStream ms = new MemoryStream(ffmpeg_thumb)) {
                        MagickImage mi = new MagickImage(ms);

                        if (mi.Orientation != OrientationType.Undefined)
                            mi.AutoOrient();

                        if (CurrentConfig.server["gallery"]["thumbnail_compression"].ToBool()) {
                            Conversion.Image.ConvertToJpeg(mi, (uint)thumb_compression_quality);
                            img_data = mi.ToByteArray();
                            lock (thumbnail_cache) thumbnail_cache.Add(request.file.FullName, ("image/jpeg", img_data));
                        } else {
                            Conversion.Image.ConvertToPng(mi);
                            img_data = mi.ToByteArray();
                            lock (thumbnail_cache) thumbnail_cache.Add(request.file.FullName, ("image/png", img_data));
                        }
                    }

                } catch (Exception ex) {
                    Logging.Error($"{request.file.Name} :: {ex.Message}");
                    request.context.Response.Close();
                }
            }

            //pull byte array from the cache and set up a few requirements
            lock (thumbnail_cache) {
                if (thumbnail_cache.ContainsKey(request.file.FullName)) {
                    request.thumbnail = thumbnail_cache[request.file.FullName].data;
                    request.response.ContentType = thumbnail_cache[request.file.FullName].mime;
                    request.response.ContentLength64 = request.thumbnail.LongLength;
                } else goto cache_fail;

            }

            try {
                MemoryStream ms = new MemoryStream(request.thumbnail, false);
                ms.CopyToAsync(request.response.OutputStream, CurrentConfig.cancellation_token).ContinueWith(r => {
                    //success
                    request.response.StatusCode = (int)HttpStatusCode.OK;
                    request.response.StatusDescription = "200 OK";

                    request.response.Close();

                    ms.Close();
                    ms.Dispose();

                    if (CurrentConfig.LogLevel == Logging.LogLevel.ALL)
                        Logging.ThreadMessage($"Sent thumb: {request.file.Name}", $"THUMB:{request.thread_id}", request.thread_id);
                });

            } catch (HttpListenerException ex) {
                Logging.Error($"{request.file.Name} :: {ex.Message}");
            }

        }
    }
}
