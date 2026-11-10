using Foxfire.Server.Security;
using Microsoft.AspNetCore.Http;

namespace Foxfire.UnitTests.Security;

public sealed class RequestPathRedactorTests
{
    [Theory]
    [InlineData("/embed/v1.abc.def", "/embed/***")]
    [InlineData("/stream/v1.abc.def/index.m3u8", "/stream/***/index.m3u8")]
    [InlineData("/stream/v1.abc.def/seg_00001.m4s", "/stream/***/seg_00001.m4s")]
    [InlineData("/STREAM/tok/refresh", "/STREAM/***/refresh")]
    [InlineData("/api/v1/videos", "/api/v1/videos")]
    [InlineData("/embed", "/embed")]
    [InlineData("", "/")]
    public void Token_segments_are_redacted(string path, string expected) =>
        Assert.Equal(expected, RequestPathRedactor.Redact(new PathString(path)));
}
