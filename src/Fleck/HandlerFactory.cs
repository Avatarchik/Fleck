using System;
using Fleck.Handlers;

namespace Fleck
{
    public class HandlerFactory
    {
        public static IHandler BuildHandler(
            WebSocketHttpRequest request,
            Action<string> onMessage,
            Action onClose,
            BinaryDataHandler onBinary,
            BinaryDataHandler onPing,
            BinaryDataHandler onPong)
        {
            var version = GetVersion(request);
            
            switch (version)
            {
                case "7":
                case "8":
                case "13":
                    return new Hybi13Handler(request, onMessage, onClose, onBinary, onPing, onPong);
            }
            
            throw new WebSocketException(WebSocketStatusCodes.ProtocolError);
        }
        
        public static string GetVersion(WebSocketHttpRequest request) 
        {
            string version;
            if (request.Headers.TryGetValue("Sec-WebSocket-Version", out version))
                return version;
                
            if (request.Headers.TryGetValue("Sec-WebSocket-Draft", out version))
                return version;
            
            if (request.Headers.ContainsKey("Sec-WebSocket-Key1"))
                return "76";

            return "75";
        }
    }
}

