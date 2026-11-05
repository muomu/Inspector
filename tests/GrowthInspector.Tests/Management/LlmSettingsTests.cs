using System.Net;
using System.Text;
using GrowthInspector.Management;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrowthInspector.Tests.Management;

/// <summary>
/// 模型接入（LLM API key）：加密落库、掩码回显、清除保留地址、三字段必填，
/// 以及「测试连接」用表单值 + 已存 key（掩码回退）的分类提示。
/// </summary>
public sealed class LlmSettingsTests
{
    [Fact]
    public async Task GetLlmSettings_BeforeFirstSave_ReturnsAppSettingsDefaultsAndNoKey()
    {
        await using var context = await ManagementTestContext.CreateAsync();

        var view = await context.Management.GetLlmSettingsAsync();

        Assert.Null(view.ApiKeyMask);
        Assert.False(view.HasApiKey);
        Assert.False(view.IsConfigured);
        Assert.Null(view.BaseUrl);
        Assert.Null(view.Model);
        // 出厂预填值来自 appsettings 的 DeepSeek 节，仍需显式保存才生效。
        Assert.Equal("https://api.deepseek.com", view.DefaultBaseUrl);
        Assert.Equal("deepseek-flash", view.DefaultModel);
    }

    [Fact]
    public async Task SaveLlmSettings_MasksKeyOnRead_AndStoresCiphertextOnDisk()
    {
        await using var context = await ManagementTestContext.CreateAsync();

        await context.Management.SaveLlmSettingsAsync(
            new SaveLlmSettingsRequest("sk-1234567890abcd", "https://api.example.test", "m-1"));

        var view = await context.Management.GetLlmSettingsAsync();
        Assert.True(view.HasApiKey);
        Assert.True(view.IsConfigured);
        Assert.Equal("****abcd", view.ApiKeyMask);
        Assert.Equal("https://api.example.test", view.BaseUrl);
        Assert.Equal("m-1", view.Model);
        Assert.NotNull(view.UpdatedAt);

        // 落库的是密文：明文 key 不能出现在数据库行里。
        await using var db = await context.DbFactory.CreateDbContextAsync();
        var row = await db.LlmSettings.SingleAsync();
        Assert.False(string.IsNullOrWhiteSpace(row.ProtectedApiKey));
        Assert.DoesNotContain("sk-1234567890abcd", row.ProtectedApiKey!);

        // 解密路径正常：存储层读出来就是原 key。
        Assert.Equal("sk-1234567890abcd", (await context.LlmSettings.GetAsync()).ApiKey);
    }

    [Fact]
    public async Task SaveLlmSettings_WithMaskedOrBlankKey_KeepsExistingKey()
    {
        await using var context = await ManagementTestContext.CreateAsync();
        await context.Management.SaveLlmSettingsAsync(
            new SaveLlmSettingsRequest("sk-original-key", "https://api.example.test", "m-1"));

        var masked = (await context.Management.GetLlmSettingsAsync()).ApiKeyMask;
        await context.Management.SaveLlmSettingsAsync(new SaveLlmSettingsRequest(masked, "https://api.example.test", "m-2"));
        Assert.Equal("sk-original-key", (await context.LlmSettings.GetAsync()).ApiKey);

        await context.Management.SaveLlmSettingsAsync(new SaveLlmSettingsRequest(null, "https://api.example.test", "m-3"));
        Assert.Equal("sk-original-key", (await context.LlmSettings.GetAsync()).ApiKey);
        Assert.Equal("m-3", (await context.Management.GetLlmSettingsAsync()).Model);
    }

    [Fact]
    public async Task ClearLlmApiKey_KeepsBaseUrlAndModel_AndMarksNotConfigured()
    {
        await using var context = await ManagementTestContext.CreateAsync();
        await context.Management.SaveLlmSettingsAsync(
            new SaveLlmSettingsRequest("sk-original-key", "https://api.example.test", "m-1"));

        await context.Management.ClearLlmApiKeyAsync();

        var view = await context.Management.GetLlmSettingsAsync();
        Assert.False(view.HasApiKey);
        Assert.False(view.IsConfigured);
        Assert.Equal("https://api.example.test", view.BaseUrl);
        Assert.Equal("m-1", view.Model);
        var snapshot = await context.LlmSettings.GetAsync();
        Assert.Null(snapshot.ApiKey);
    }

    [Fact]
    public async Task SaveLlmSettings_WithoutKey_WhenNeverConfigured_Throws()
    {
        await using var context = await ManagementTestContext.CreateAsync();

        var exception = await Assert.ThrowsAsync<ManagementException>(
            () => context.Management.SaveLlmSettingsAsync(
                new SaveLlmSettingsRequest(null, "https://api.example.test", "m-1")));

        Assert.Contains("API key", exception.Message);
    }

    [Fact]
    public async Task SaveLlmSettings_RejectsNonHttpBaseUrl()
    {
        await using var context = await ManagementTestContext.CreateAsync();

        var exception = await Assert.ThrowsAsync<ManagementException>(
            () => context.Management.SaveLlmSettingsAsync(
                new SaveLlmSettingsRequest("sk-x", "api.example.test", "m-1")));

        Assert.Contains("BaseUrl", exception.Message);
    }

    [Fact]
    public async Task TestLlmConnection_UsesFormValues_AndFallsBackToStoredKeyWhenMasked()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        await using var context = await ManagementTestContext.CreateAsync(
            services => services.AddSingleton(new LlmConnectionProbe(handler)));

        await context.Management.SaveLlmSettingsAsync(
            new SaveLlmSettingsRequest("sk-stored-4321", "https://api.example.test", "m-1"));
        var masked = (await context.Management.GetLlmSettingsAsync()).ApiKeyMask;

        var result = await context.Management.TestLlmConnectionAsync(
            new TestLlmConnectionRequest(masked, "https://api.example.test", "m-1"));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Bearer sk-stored-4321", handler.LastAuthorization);
        Assert.Equal("https://api.example.test/chat/completions", handler.LastUri!.ToString());
        Assert.Contains("m-1", result.Message);
        // 试连不改动已保存的配置。
        Assert.Equal("****4321", (await context.Management.GetLlmSettingsAsync()).ApiKeyMask);
    }

    [Fact]
    public async Task TestLlmConnection_MapsUnauthorizedToKeyHint()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid api key\"}");
        await using var context = await ManagementTestContext.CreateAsync(
            services => services.AddSingleton(new LlmConnectionProbe(handler)));

        var result = await context.Management.TestLlmConnectionAsync(
            new TestLlmConnectionRequest("sk-bad-key", "https://api.example.test", "m-1"));

        Assert.False(result.Ok);
        Assert.Contains("401", result.Message);
        Assert.Contains("key", result.Message);
    }

    [Fact]
    public async Task TestLlmConnection_MapsUnknownModelToAddressOrModelHint()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, "{\"error\":\"model not found\"}");
        await using var context = await ManagementTestContext.CreateAsync(
            services => services.AddSingleton(new LlmConnectionProbe(handler)));

        var result = await context.Management.TestLlmConnectionAsync(
            new TestLlmConnectionRequest("sk-key", "https://api.example.test", "no-such-model"));

        Assert.False(result.Ok);
        Assert.Contains("模型名", result.Message);
    }

    /// <summary>记录请求地址与 Authorization 头的假处理器；不联网。</summary>
    private sealed class RecordingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastUri = request.RequestUri;

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
