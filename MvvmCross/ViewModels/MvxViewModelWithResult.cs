using Microsoft.Extensions.Logging;
using MvvmCross.Navigation;

namespace MvvmCross.ViewModels
{
    //this is only marker class 
    public class ViewModelResult
    {
    }

    public abstract class MvxViewModelWithResult<TResult> : MvxNavigationViewModel, ITransactionRequesterViewModel<TResult> where TResult : ViewModelResult, new()
    {
        protected MvxViewModelWithResult(
            ILoggerFactory logProvider,
            IMvxNavigationService navigationService
        ) : base(logProvider, navigationService)
        {
        }
        protected TResult Result { get; private set; }

        public void OnResult(TResult result)
        {
            Log.LogDebug("OnResult was called");
            Result = result;
        }
        public override void ViewAppeared()
        {
            base.ViewAppeared();
            if (Result != null)
            {
                AfterResultReceived();
            }
        }

        protected abstract void AfterResultReceived();
    }

}
