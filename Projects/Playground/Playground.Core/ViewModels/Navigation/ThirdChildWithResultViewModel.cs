// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MS-PL license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using MvvmCross.Commands;
using MvvmCross.Navigation;
using MvvmCross.ViewModels;

namespace Playground.Core.ViewModels
{
    public class ThirdChildResult : ViewModelResult
    {
        public string Text { get; set; }
    }

    public class ThirdChildWithResultViewModel : MvxNavigationViewModel
    {
        public ThirdChildWithResultViewModel(ILoggerFactory logFactory, IMvxNavigationService navigationService)
            : base(logFactory, navigationService)
        {
            ShowNestedChildCommand = new MvxAsyncCommand(() => NavigationService.Navigate<NestedChildViewModel>());

            CloseCommand = new MvxAsyncCommand(() => NavigationService.CloseWithResult(this, new SecondChildResult()
            {
                Text = "This is a result from third child"
            }));
        }

        public IMvxAsyncCommand ShowNestedChildCommand { get; }

        public IMvxAsyncCommand CloseCommand { get; }
    }
}
