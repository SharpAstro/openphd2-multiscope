using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace TianWen.OpenPHD2MultiScope.Tests;

public class JsonRpcTests
{
    [Fact]
    public void ANamedDitherRequestParses()
    {
        RpcRequest.TryParse("""{"method":"dither","params":{"amount":20.0,"settle":{"pixels":2.0,"time":5.0,"timeout":80.0},"raOnly":true},"id":7}""",
            out var request, out var error, out _).ShouldBeTrue();
        using (request)
        {
            error.ShouldBeNull();
            request.ShouldNotBeNull();
            request.Method.ShouldBe("dither");
            request.IsNotification.ShouldBeFalse();
            request.TryGetParam("amount", 0, out var amount).ShouldBeTrue();
            amount.GetDouble().ShouldBe(20.0);
            request.TryGetParam("raOnly", 1, out var raOnly).ShouldBeTrue();
            raOnly.GetBoolean().ShouldBeTrue();
        }
    }

    [Fact]
    public void APositionalRequestParsesByPosition()
    {
        RpcRequest.TryParse("""{"jsonrpc":"2.0","method":"dither","params":[10,false,{"pixels":1.5,"time":8,"timeout":40}],"id":"abc"}""",
            out var request, out _, out _).ShouldBeTrue();
        using (request)
        {
            request.ShouldNotBeNull();
            request.TryGetParam("amount", 0, out var amount).ShouldBeTrue();
            amount.GetDouble().ShouldBe(10);
            request.TryGetParam("settle", 2, out var settle).ShouldBeTrue();
            settle.GetProperty("pixels").GetDouble().ShouldBe(1.5);
            request.TryGetParam("missing", 3, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void ARequestWithoutIdIsANotification()
    {
        RpcRequest.TryParse("""{"method":"loop"}""", out var request, out _, out _).ShouldBeTrue();
        using (request)
        {
            request.ShouldNotBeNull();
            request.IsNotification.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("not json at all", -32700)]
    [InlineData("[1,2,3]", -32600)]
    [InlineData("""{"id":3}""", -32600)]
    [InlineData("""{"method":"","id":3}""", -32600)]
    [InlineData("""{"method":42,"id":3}""", -32600)]
    public void ALineThatIsNotARequestIsAnErrorNotAThrow(string line, int expectedCode)
    {
        RpcRequest.TryParse(line, out var request, out var error, out _).ShouldBeFalse();
        request.ShouldBeNull();
        error.ShouldNotBeNull();
        error.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void AnInvalidRequestStillEchoesItsId()
    {
        RpcRequest.TryParse("""{"id":9,"method":null}""", out _, out _, out var id).ShouldBeFalse();
        id.ShouldNotBeNull();
        id.Value.GetInt32().ShouldBe(9);
    }

    [Fact]
    public void AResultEchoesANumericIdAndEndsInCrLf()
    {
        using var doc = JsonDocument.Parse("12");
        var line = Encoding.UTF8.GetString(RpcResponse.Result(doc.RootElement, 0));

        line.ShouldBe("""{"jsonrpc":"2.0","result":0,"id":12}""" + "\r\n");
    }

    [Fact]
    public void AnErrorEchoesAStringId()
    {
        using var doc = JsonDocument.Parse("\"x-1\"");
        var line = Encoding.UTF8.GetString(RpcResponse.Error(doc.RootElement, RpcError.MethodNotFound("find_star")));

        using var reply = JsonDocument.Parse(line);
        reply.RootElement.GetProperty("id").GetString().ShouldBe("x-1");
        reply.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        (reply.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "").ShouldContain("find_star");
    }

    [Fact]
    public void AnErrorWithoutAnIdHasANullId()
    {
        var line = Encoding.UTF8.GetString(RpcResponse.Error(null, RpcError.ParseError));

        using var reply = JsonDocument.Parse(line);
        reply.RootElement.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
