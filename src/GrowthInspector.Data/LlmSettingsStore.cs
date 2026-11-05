using GrowthInspector.Core.Abstractions;
using GrowthInspector.Core.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace GrowthInspector.Data;

/// <summary>
/// 应用级单行 LLM 配置的读写：API key 用 DataProtection 加密后落库（明文只在内存快照与请求头里出现），
/// BaseUrl 与模型明文存储。密钥环位置由宿主指定（Web 在 <c>Program.cs</c> 指到 <c>data/protection-keys</c>）。
/// </summary>
public sealed class LlmSettingsStore(
    IDbContextFactory<GrowthInspectorDbContext> dbContextFactory,
    IDataProtectionProvider dataProtectionProvider) : ILlmSettingsStore
{
    private const int SingletonRowId = 1;

    /// <summary>密钥保护用途串：改动它会使已有密文无法解密（等同于视为未配置）。</summary>
    private const string ProtectorPurpose = "GrowthInspector.LlmSettings.ApiKey.v1";

    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    public async Task<LlmSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.LlmSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == SingletonRowId, cancellationToken);

        return row is null
            ? new LlmSettingsSnapshot(null, null, null, null)
            : new LlmSettingsSnapshot(Decrypt(row.ProtectedApiKey), row.BaseUrl, row.Model, row.UpdatedAt);
    }

    public async Task SaveAsync(LlmSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.LlmSettings.SingleOrDefaultAsync(x => x.Id == SingletonRowId, cancellationToken);
        if (row is null)
        {
            row = new LlmSetting { Id = SingletonRowId };
            db.LlmSettings.Add(row);
        }

        // 空 key 表示「保持原值」：只有显式提交非空 key 才重新加密写入。
        if (!string.IsNullOrWhiteSpace(update.ApiKey))
        {
            row.ProtectedApiKey = protector.Protect(update.ApiKey.Trim());
        }

        row.BaseUrl = update.BaseUrl;
        row.Model = update.Model;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.LlmSettings.SingleOrDefaultAsync(x => x.Id == SingletonRowId, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.ProtectedApiKey = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 解密失败（密钥环被换掉或删除）按「未配置」处理：执行侧明确报缺 key，
    /// 页面提示重新填写，不把密文当 key 使用。
    /// </summary>
    private string? Decrypt(string? protectedApiKey)
    {
        if (string.IsNullOrWhiteSpace(protectedApiKey))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(protectedApiKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
