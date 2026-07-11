using System.Collections.Generic;

namespace WeChatSidekick
{
    public class BackendRequest
    {
        public string Id { get; set; }
        public string Method { get; set; }
        public Dictionary<string, object> Params { get; set; }
    }

    public class BackendResponse
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public string Method { get; set; }
        public bool Ok { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
    }

    public class WeChatMessageDto
    {
        public string Sender { get; set; }
        public string Text { get; set; }
    }

    public class WeChatStateDto
    {
        public string Type { get; set; }
        public string ChatName { get; set; }
        public List<string> Messages { get; set; }
        public int[] WechatRect { get; set; }
    }
}
