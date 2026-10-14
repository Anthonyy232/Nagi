using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Presentation.ViewModels;
using Xunit;

namespace Nagi.Presentation.Tests;

public class SearchableViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingSearch_CancelsRunningQuery_AndResetCancelsReplacement(bool immediate)
    {
        var viewModel = new SearchViewModel();
        var firstSearch = viewModel.SearchAsync();
        Task? replacement = null;
        try
        {
            var firstToken = await viewModel.Searches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.SearchTerm = "replacement";
            if (immediate)
                replacement = viewModel.SearchAsync();

            var replacementToken = await viewModel.Searches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            firstToken.IsCancellationRequested.Should().BeTrue();
            replacementToken.IsCancellationRequested.Should().BeFalse();

            viewModel.ResetState();

            replacementToken.IsCancellationRequested.Should().BeTrue();
            viewModel.SearchTerm.Should().Be("replacement");
        }
        finally
        {
            viewModel.ResetState();
            await firstSearch;
            if (replacement != null)
                await replacement;
        }
    }

    private sealed class SearchViewModel() : SearchableViewModelBase(new ImmediateDispatcher(), NullLogger.Instance)
    {
        public Channel<CancellationToken> Searches { get; } = Channel.CreateUnbounded<CancellationToken>();

        protected override async Task ExecuteSearchAsync(CancellationToken token)
        {
            await Searches.Writer.WriteAsync(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
