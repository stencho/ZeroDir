﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;

namespace ZeroDir {
    internal static class FileListing {
        public static string BuildListing(string directory, string prefix, string uri_path, string share_name) {
            string result = "";
            int file_count;

            uri_path = Uri.UnescapeDataString(uri_path);

            while (uri_path.StartsWith('/')) {
                uri_path = uri_path.Remove(0,1);
            }

            while (prefix.EndsWith('/')) {
                prefix = prefix.Remove(prefix.Length - 1, 1);
            }

            List<string> listing = new List<string>();

            if (!directory.EndsWith('/')) directory = directory + "/";

            Logging.Message($"listing directory: {directory}{uri_path}");
            DirectoryInfo dirInfo = new DirectoryInfo($"{directory}{uri_path}");
            if (!dirInfo.Exists) return "";

            var directories = dirInfo.GetDirectories();
            var files = dirInfo.GetFiles();

            string up_dir = uri_path;
            int slash_i = up_dir.LastIndexOf('/');
            if (slash_i > -1) up_dir = up_dir.Remove(slash_i);
            else up_dir = "";
            //if (up_dir.Length > 0)
                result += $"<p><a href=\"http://{prefix}/{up_dir}\">.. [ {up_dir} ])</a></p>";
            //else
                //result += $"<p>{up_dir}</p>";

            
            Logging.Message($"up_dir: {up_dir}");
            bool show_dirs = false;
            if (CurrentConfig.shares[share_name].ContainsKey("show_directories")) {
                show_dirs = CurrentConfig.shares[share_name]["show_directories"].get_bool();
            }
            bool using_extensions = false;
            string[] extensions = null;
            if (CurrentConfig.shares[share_name].ContainsKey("extensions")) {
                extensions = CurrentConfig.shares[share_name]["extensions"].ToString().Trim().Split(" ");
                using_extensions = true;
                for (int i =  0; i < extensions.Length; i++) {
                    extensions[i] = extensions[i].Trim();
                    extensions[i] = extensions[i].Replace(".", "");
                }
            }

            if (show_dirs) {
                foreach (var dir in directories) {
                    string n = uri_path;
                    while (n.EndsWith('/')) n = n.Remove(n.Length - 1, 1);
                    if (n.Length > 0) n = n.Insert(0, "/");
                    listing.Add($"{dir.Name}");
                    result += $"<p><a href=\"http://{prefix}/{share_name}/{n}/{Uri.EscapeDataString($"{dir.Name}")}\">{dir.Name}</a></p>";
                }
            }
            foreach (var file in files) {
                string n = uri_path;
                string f = file.Name;

                var ext = new FileInfo(f).Extension.Replace(".", "");

                if (using_extensions && !extensions.Contains(ext)) {
                    continue;
                }

                while (n.EndsWith('/')) n = n.Remove(n.Length-1, 1);
                while (f.StartsWith('/')) f = f.Remove(0, 1);
                if (n.Length > 0) n = n.Insert(0, "/");

                listing.Add($"{f}");
                //Logging.Message($"{n} {f}  http://{prefix}/{share_name}{n}/{Uri.EscapeDataString($"{f}")}");
                result += $"<p><a href=\"http://{prefix}/{share_name}{n}/{Uri.EscapeDataString($"{f}")}\">{f}</a></p>";
            }

            return result;
        }
    }
}
