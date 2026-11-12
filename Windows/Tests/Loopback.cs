using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Translated.Tests
{
    internal static class Loopback
    {
        // Exercise the shipping HTTP client, including redirect handling, with no provider account.
        internal static async Task Check(bool redirect)
        {
            var server = new TcpListener(IPAddress.Loopback,0);
            server.Start();
            try
            {
                int port = ((IPEndPoint)server.LocalEndpoint).Port;
                var p = Provider.Default(Provider.Custom); p.BaseUrl = "http://127.0.0.1:" + port + "/v1"; p.Model = "fixture";
                var reply = Task.Run(async () => {
                    using (var connection = await server.AcceptTcpClientAsync())
                    using (var stream = connection.GetStream())
                    {
                        stream.ReadTimeout = 4000;
                        var bytes = new List<byte>();
                        while (bytes.Count < 16000)
                        {
                            int b = stream.ReadByte(); if (b < 0) throw new EndOfStreamException(); bytes.Add((byte)b);
                            int n = bytes.Count;
                            if (n >= 4 && bytes[n-4]==13 && bytes[n-3]==10 && bytes[n-2]==13 && bytes[n-1]==10) break;
                        }
                        string headers = Encoding.ASCII.GetString(bytes.ToArray());
                        if (!headers.StartsWith("POST /v1/chat/completions ") || headers.IndexOf("Authorization:",StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new InvalidDataException("Unexpected endpoint or credentials");
                        int length = 0;
                        foreach (var line in headers.Split(new[] { "\r\n" },StringSplitOptions.RemoveEmptyEntries))
                            if (line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) length=int.Parse(line.Substring(15));
                        if (headers.IndexOf("100-continue",StringComparison.OrdinalIgnoreCase)>=0)
                        {
                            var ack=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); stream.Write(ack,0,ack.Length);
                        }
                        if (length <= 0 || length > 65536) throw new InvalidDataException("Invalid request length");
                        var request = new byte[length];
                        for (int read=0; read<length;)
                        {
                            int n=stream.Read(request,read,length-read); if (n==0) throw new EndOfStreamException(); read+=n;
                        }
                        var body = Json.Read<Request>(request);
                        var payload = Json.Read<Payload>(Encoding.UTF8.GetBytes(body.messages[1].content));
                        if (payload.text != "Hello" || body.model != "fixture") throw new InvalidDataException("Incorrect HTTP body");
                        var json=Encoding.UTF8.GetBytes("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"你好\"}}]}");
                        string status = redirect ? "307 Temporary Redirect\r\nLocation: /unexpected" : "200 OK";
                        var head=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: "+json.Length+"\r\n\r\n");
                        stream.Write(head,0,head.Length); stream.Write(json,0,json.Length);
                    }
                });
                using (var stop=new CancellationTokenSource(6000))
                {
                    string result=null; bool refused=false;
                    try { result=await Translator.Translate("Hello",p,"",false,stop.Token); }
                    catch (InvalidOperationException e) when (redirect && e.Message.Contains("重定向")) { refused=true; }
                    await reply;
                    if (redirect ? !refused : result!="你好") throw new Exception("Unexpected loopback response");
                }
            }
            finally { server.Stop(); }
        }
    }
}
