// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MS-PL license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using MvvmCross.Commands;
using MvvmCross.Navigation;
using MvvmCross.ViewModels;

namespace Playground.Core.ViewModels
{
    public class ChildWithResultViewModel : MvxViewModelWithResult<ViewModelResult>
    {
        public string Text { get => _text; set => SetProperty(ref _text, value); }
        private string _text;

        public ChildWithResultViewModel(ILoggerFactory logProvider, IMvxNavigationService navigationService)
            : base(logProvider, navigationService)
        {
            CloseCommand = new MvxAsyncCommand(() => NavigationService.Close(this));

            ShowSecondChildCommand = new MvxAsyncCommand(() => NavigationService.NavigateForResult<SecondChildWithResultViewModel, ViewModelResult>(this));

            ShowThirdChildCommand = new MvxAsyncCommand(() => NavigationService.NavigateForResult<ThirdChildWithResultViewModel, ViewModelResult>(this));

            ShowBrokenChildWithResultCommand = new MvxAsyncCommand(async () =>
            {
                var response = await NavigationService.Navigate<BrokenChildWithResultViewModel, string, BrokenChildViewResult>("this is a parameter").ConfigureAwait(false);
                // this won't be ever called after state restore cycle
                if (response != null)
                {
                    InvokeOnMainThread(() => Text = response.Text);
                }
            });

            ShowRootCommand = new MvxAsyncCommand(() => NavigationService.Navigate<RootViewModel>());
        }

        protected override void AfterResultReceived()
        {
            // this demonstrates how you can handle multiple different results
            if (Result is SecondChildResult secondChildResult)
            {
                //handle second child result 
                InvokeOnMainThread(() => Text = secondChildResult.Text);
            }

            if (Result is ThirdChildResult thirdChildResult)
            {
                //handle third child result 
                InvokeOnMainThread(() => Text = thirdChildResult.Text);
            }
        }

        public IMvxAsyncCommand CloseCommand { get; private set; }

        public IMvxAsyncCommand ShowSecondChildCommand { get; private set; }
        public IMvxAsyncCommand ShowThirdChildCommand { get; }
        public IMvxAsyncCommand ShowBrokenChildWithResultCommand { get; }
        public IMvxAsyncCommand ShowRootCommand { get; private set; }
    }
}
