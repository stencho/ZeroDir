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

namespace ZeroDir.DBThreads {
    public class ThumbnailRequest {
        public FileInfo file;
        public byte[] thumbnail = null;

        public string mime_type = "";
        
        public bool thumbnail_ready = false;
        public bool thread_dispatched = false;

        public int thread_id = 0;

        public HttpListenerResponse response;

        public FolderServer parent_server;

        public ThumbnailRequest(FileInfo file, HttpListenerResponse response, FolderServer parent_server, string mime_type) {
            this.file = file;
            this.response = response;
            this.parent_server = parent_server;
            this.mime_type = mime_type;
        }
    }

    public static class ThumbnailThreadPool {
        //thread for handling thumbnail requests
        static Thread request_thread = new Thread(handle_requests);

        //threads for building thumbnails
        static volatile Thread[] build_threads = new Thread[build_thread_count];
        static volatile ThumbnailRequest[] current_requests = new ThumbnailRequest[build_thread_count];
        static int build_thread_count = 32;

        //the queue that the dispatcher uses for starting threads
        static Queue<ThumbnailRequest> request_queue = new Queue<ThumbnailRequest>(build_thread_count*4);

        //cache for thumbnails which have been loaded at least once
        volatile static Dictionary<string, (string mime, byte[] data)> thumbnail_cache = new Dictionary<string, (string mime, byte[] data)>();

        public static void Start() {
            build_thread_count = CurrentConfig.server["gallery"]["thumbnail_builder_threads"].get_int();
            current_requests = new ThumbnailRequest[build_thread_count];
            build_threads = new Thread[build_thread_count];

            Logging.ThreadMessage($"Starting dispatcher thread, using {build_thread_count} builder threads", "THUMB", 0);
            request_thread.Start();
        }

        public static void RequestThumbnail(string filename, HttpListenerResponse response, FolderServer parent_server, string mime_type) {
            FileInfo f = new FileInfo(filename);
            if (f.Exists) {
                RequestThumbnail(f, response, parent_server, mime_type);
            } else return;
        }

        public static void RequestThumbnail(FileInfo file, HttpListenerResponse response, FolderServer parent_server, string mime_type) {
            //Logging.ThreadMessage($"Requesting thumbnail for {file.Name}", "THUMB", 0);
            request_queue.Enqueue(new ThumbnailRequest(file, response, parent_server, mime_type));
        }

        //main dispatch thread loop
        static void handle_requests() {         
            while (true) {
                while (request_queue.Count > 0) {
                    for (int t = 0; t < current_requests.Length; t++) {
                        if (current_requests[t] == null) {
                            current_requests[t] = request_queue.Dequeue();
                            if (current_requests[t] != null) {
                                current_requests[t].thread_id = t;
                                current_requests[t].thread_dispatched = true;

                                build_threads[t] = new Thread(build_thumbnail);
                                build_threads[t].Start(current_requests[t]);
                            }           
                            break;
                        }
                    }
                }

                Thread.Sleep(100);
            }
        }

        internal static byte[] get_first_video_frame_from_ffmpeg(ThumbnailRequest req) {

            var stream_output = new MemoryStream();

            var stream_video = FFMpegArguments
                .FromFileInput(req.file)
                .OutputToPipe(new StreamPipeSink(stream_output), options => 
                    options.WithFrameOutputCount(1)
                    .WithVideoCodec(VideoCodec.Png)
                    .Resize(128,128)
                    .ForceFormat("image2pipe")
                    )
                .ProcessSynchronously();

            return stream_output.ToArray();
        }

        static void build_thumbnail(object request) {
            ThumbnailRequest req = (ThumbnailRequest)request;
            //Logging.ThreadMessage($"Building thumbnail for {req.file.Name}", "THUMB", req.thread_id);

            if (thumbnail_cache.ContainsKey(req.file.Name)) {
                //cache hit, do nothing
            } else if (req.mime_type.StartsWith("image")) {
                MagickImage mi = new MagickImage(req.file.FullName);
                mi.Resize(128, 128);

                lock (thumbnail_cache) {
                    thumbnail_cache.Add(req.file.Name, ("image/bmp", mi.ToByteArray()));
                }

            } else if (req.mime_type.StartsWith("video")) {
                var thumb = get_first_video_frame_from_ffmpeg(req);

                lock (thumbnail_cache) {
                    thumbnail_cache.Add(req.file.Name, ("image/png", thumb));
                }
            }

            req.thumbnail = thumbnail_cache[req.file.Name].data;
            req.response.ContentType = thumbnail_cache[req.file.Name].mime;

            req.response.ContentLength64 = req.thumbnail.LongLength;

            req.parent_server.current_sub_thread_count++;
            req.response.OutputStream.BeginWrite(req.thumbnail, 0, req.thumbnail.Length, result => {
                req.response.StatusCode = (int)HttpStatusCode.OK;
                req.response.StatusDescription = "400 OK";
                req.response.OutputStream.Close();
                req.response.Close();
                //Logging.ThreadMessage($"Finished writing thumbnail for {req.file.Name}", "THUMB", req.thread_id);
                req.parent_server.current_sub_thread_count--;
                lock (current_requests) {
                    current_requests[req.thread_id] = null;
                }
            }, req.response);            
        }
    }
}