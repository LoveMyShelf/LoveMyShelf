using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LoveMyShelf {
    internal static class AppInfo {
        // Keep in step with the GitHub release tag (v1.2.0 and so on).
        internal const string Version="1.0.0";
        internal const string ReleasesPage="https://github.com/LoveMyShelf/LoveMyShelf/releases/latest";
        const string LatestApi="https://api.github.com/repos/LoveMyShelf/LoveMyShelf/releases/latest";

        // Asks GitHub once for the newest release tag. Sends nothing but a plain request;
        // returns null when offline, when GitHub can't be reached, or when this copy is current.
        internal static Task<string> NewerVersionAsync() {
            return Task.Run(()=>{
                try {
                    ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072; // TLS 1.2
                    var request=(HttpWebRequest)WebRequest.Create(LatestApi);
                    request.UserAgent="LoveMyShelf";request.Accept="application/vnd.github+json";
                    request.Timeout=8000;request.ReadWriteTimeout=8000;
                    string json;
                    using(var response=request.GetResponse())
                    using(var reader=new StreamReader(response.GetResponseStream()))json=reader.ReadToEnd();
                    var match=Regex.Match(json,"\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
                    if(!match.Success)return null;
                    return IsNewer(match.Groups[1].Value,Version)?match.Groups[1].Value:null;
                } catch(Exception) {return null;}
            });
        }

        internal static bool IsNewer(string candidate,string current) {
            Version a,b;
            return System.Version.TryParse(candidate,out a) && System.Version.TryParse(current,out b) && a>b;
        }
    }
}
