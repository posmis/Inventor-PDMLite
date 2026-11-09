using PdmLite.Modules.Library.Checks;

namespace PdmLite.Library.Tests;

public class ContentCenterPromptAttemptsTests
{
    [Theory]
    [InlineData("catalog")]
    [InlineData("items")]
    [InlineData("check")]
    [InlineData("dialog")]
    public async Task FailureBeforeShowing_AllowsRetry(string failure)
    {
        var attempts = new ContentCenterPromptAttempts();
        var calls = new List<string>();
        Task Attempt(Action shown)
        {
            foreach (var stage in new[] { "catalog", "items", "check", "dialog" })
            {
                calls.Add(stage);
                if (stage == failure) throw new IOException(stage);
            }
            shown();
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<IOException>(() => attempts.RunAsync("asm|part", Attempt));
        var prompts = 0;
        await attempts.RunAsync("ASM|PART", shown => { prompts++; shown(); return Task.CompletedTask; });
        await attempts.RunAsync("asm|part", _ => throw new Exception("already shown"));
        Assert.Equal(1, prompts);
        Assert.Equal(failure, calls[^1]);
    }

    [Theory]
    [InlineData(false)] // «Нет» или информационное предупреждение без замены.
    [InlineData(true)] // Вопрос показан, но последующая замена завершилась ошибкой.
    public async Task ShownQuestion_IsNotRepeated_EvenIfReplacementFails(bool failReplacement)
    {
        var attempts = new ContentCenterPromptAttempts();
        var prompts = 0;
        Task Attempt(Action shown)
        {
            prompts++;
            shown();
            if (failReplacement) throw new IOException("replacement failed");
            return Task.CompletedTask;
        }
        if (failReplacement) await Assert.ThrowsAsync<IOException>(() => attempts.RunAsync("asm|part", Attempt));
        else await attempts.RunAsync("asm|part", Attempt);
        await attempts.RunAsync("asm|part", Attempt);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task ConcurrentDuplicate_IsSkipped_ButOtherAssemblyIsIndependent()
    {
        var attempts = new ContentCenterPromptAttempts();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = attempts.RunAsync("asm|part", async shown =>
        {
            started.SetResult();
            await finish.Task;
            shown();
        });
        await started.Task;
        await attempts.RunAsync("ASM|PART", _ => throw new Exception("duplicate during check"));
        var other = 0;
        await attempts.RunAsync("other|part", shown => { other++; shown(); return Task.CompletedTask; });
        finish.SetResult();
        await pending;
        Assert.Equal(1, other);
    }

    [Fact]
    public async Task SkippedWithoutShowing_DoesNotConsumeQuestion()
    {
        var attempts = new ContentCenterPromptAttempts();
        await attempts.RunAsync("asm|part", _ => Task.CompletedTask);
        var shownCount = 0;
        await attempts.RunAsync("asm|part", shown => { shownCount++; shown(); return Task.CompletedTask; });
        Assert.Equal(1, shownCount);
    }
}
