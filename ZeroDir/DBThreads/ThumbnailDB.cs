﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ImageMagick;

namespace ZeroDir.DBThreads {
    public class ThumbnailDBRequest {
        public FileInfo file;
        public byte[] thumbnail = null;
        
        public bool thumbnail_ready = false;
        public bool thread_dispatched = false;

        public int thread_id = 0;

        public HttpListenerResponse response;

        public FolderServer parent_server;

        public ThumbnailDBRequest(FileInfo file, HttpListenerResponse response, FolderServer parent_server) {
            this.file = file;
            this.response = response;
            this.parent_server = parent_server;
        }
    }

    public static class ThumbnailThreadPool {
        //thread for handling thumbnail requests
        static Thread request_thread = new Thread(handle_requests);

        //threads for building thumbnails
        static volatile Thread[] build_threads = new Thread[build_thread_count];
        static volatile ThumbnailDBRequest[] current_requests = new ThumbnailDBRequest[build_thread_count];
        static int build_thread_count = 32;

        static Queue<ThumbnailDBRequest> request_queue = new Queue<ThumbnailDBRequest>();

        public static void Start() {
            build_thread_count = CurrentConfig.server["server"]["thumbnail_builder_threads"].get_int();
            current_requests = new ThumbnailDBRequest[build_thread_count];
            build_threads = new Thread[build_thread_count];

            Logging.ThreadMessage($"Starting dispatcher thread, using {build_thread_count} builder threads", "THUMB", 0);
            request_thread.Start();
        }

        public static void RequestThumbnail(string filename, HttpListenerResponse response, FolderServer parent_server) {
            FileInfo f = new FileInfo(filename);
            if (f.Exists) {
                RequestThumbnail(f, response, parent_server);
            } else return;
        }

        public static void RequestThumbnail(FileInfo file, HttpListenerResponse response, FolderServer parent_server) {
            //Logging.ThreadMessage($"Requesting thumbnail for {file.Name}", "THUMB", 0);
            request_queue.Enqueue(new ThumbnailDBRequest(file, response, parent_server));
        }

        static void handle_requests() {         
            while (true) {
                if (request_queue.Count > 0) {
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

                //Thread.Sleep(50);
            }
        }

        static void build_thumbnail(object request) {
            ThumbnailDBRequest req = (ThumbnailDBRequest)request;
            //Logging.ThreadMessage($"Building thumbnail for {req.file.Name}", "THUMB", req.thread_id);

            MagickImage mi = new MagickImage(req.file.FullName);
            mi.Resize(128, 128);

            req.thumbnail = mi.ToByteArray();
            req.response.ContentType = "image/bmp;";

            req.response.ContentLength64 = req.thumbnail.LongLength;

            req.parent_server.current_sub_thread_count++;
            req.response.OutputStream.BeginWrite(req.thumbnail, 0, req.thumbnail.Length, result => {
                req.response.StatusCode = (int)HttpStatusCode.OK;
                req.response.StatusDescription = "400 OK";
                req.response.OutputStream.Close();
                req.response.Close();
                //Logging.ThreadMessage("Finished writing thumbnail", thread_name, thread_id);
                //Logging.ThreadMessage($"Finished writing thumbnail for {req.file.Name}", "THUMB", req.thread_id);
                req.parent_server.current_sub_thread_count--;
                lock (current_requests) {
                    current_requests[req.thread_id] = null;
                }
            }, req.response);

            
        }
    }
}
