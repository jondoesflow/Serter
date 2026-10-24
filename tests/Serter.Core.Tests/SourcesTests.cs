using System.Net;
using System.Text;
using Serter.Core.Sources;

namespace Serter.Core.Tests;

public class SourcesTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ReccoBeats_combines_releases_sharing_an_isrc()
    {
        var handler = new StubHandler(_ => """
            {"content":[
              {"isrc":"GBDUW0000059","tempo":123.474,"key":6,"mode":0},
              {"isrc":"GBDUW0000059","tempo":123.494,"key":6,"mode":0},
              {"isrc":"GBDUW0000059","tempo":61.7,"key":1,"mode":1},
              {"isrc":"GBDUW0000053","tempo":122.746,"key":2,"mode":1},
              {"isrc":"NOKEY0000001","tempo":120,"key":-1,"mode":1}
            ]}
            """);
        var client = new ReccoBeatsClient(new HttpJson(new HttpClient(handler), null, TimeSpan.FromDays(1)));

        var result = await client.AudioFeaturesAsync(["gbduw0000059", "GBDUW0000053", "NOKEY0000001"], default);

        Assert.Equal(123.5, result["GBDUW0000059"].Bpm);
        Assert.Equal("11A", result["GBDUW0000059"].Camelot?.Code); // F#m
        Assert.Equal("10B", result["GBDUW0000053"].Camelot?.Code); // D major
        Assert.Null(result["NOKEY0000001"].Camelot);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Error_responses_are_not_cached()
    {
        var replies = new Queue<string>(["""{"error":{"code":4,"message":"Quota limit exceeded"}}""", """{"data":[]}"""]);
        var handler = new StubHandler(_ => replies.Dequeue());
        var json = new HttpJson(new HttpClient(handler), null, TimeSpan.FromDays(1));

        Assert.Null(await json.GetAsync("https://api.deezer.com/x", "k", default));
        Assert.NotNull(await json.GetAsync("https://api.deezer.com/x", "k", default));
        Assert.NotNull(await json.GetAsync("https://api.deezer.com/x", "k", default)); // cached now
        Assert.Equal(2, handler.Calls);
    }
}
