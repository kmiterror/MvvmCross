// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MS-PL license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using MvvmCross.Commands;
using MvvmCross.Navigation;
using MvvmCross.ViewModels;

namespace Playground.Core.ViewModels
{
    public class BrokenChildViewResult
    {
        public string Text { get; set; }
    }

    public class BrokenChildWithResultViewModel : MvxNavigationViewModel<string, BrokenChildViewResult>
    {
        private string _parameter;

        public BrokenChildWithResultViewModel(ILoggerFactory logFactory, IMvxNavigationService navigationService) : base(logFactory, navigationService) 
        {
            ShowNestedChildCommand = new MvxAsyncCommand(() => navigationService.Navigate<NestedChildViewModel>());
            CloseCommand = new MvxAsyncCommand(() => navigationService.Close(this, new BrokenChildViewResult()
            {
                Text = $"This is broken child view result and the parameters where: ${_parameter}"
            }));
        }

        public IMvxAsyncCommand ShowNestedChildCommand { get; }

        public IMvxAsyncCommand CloseCommand { get; }

        public override void Prepare(string parameter)
        {
            _parameter = parameter;
        }
    }
}
