// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MS-PL license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MvvmCross.Core;
using MvvmCross.Exceptions;
using MvvmCross.IoC;
using MvvmCross.Logging;
using MvvmCross.Navigation.EventArguments;
using MvvmCross.Presenters.Hints;
using MvvmCross.ViewModels;
using MvvmCross.Views;

namespace MvvmCross.Navigation
{
#nullable enable
    /// <inheritdoc cref="IMvxNavigationService"/>
    public class MvxNavigationService : IMvxNavigationService
    {
        private readonly IMvxIoCProvider _iocProvider;

        private readonly Lazy<ILogger?> _log = new Lazy<ILogger?>(() =>
            MvxLogHost.GetLog<MvxNavigationService>());

        public IMvxViewDispatcher ViewDispatcher { get; }

        protected Lazy<IMvxViewsContainer> ViewsContainer { get; }

        protected Dictionary<Regex, Type> Routes { get; } = new Dictionary<Regex, Type>();

        protected IMvxViewModelLoader ViewModelLoader { get; set; }

        protected ConditionalWeakTable<IMvxViewModel, TaskCompletionSource<object?>> TaskCompletionResults { get; } =
            new ConditionalWeakTable<IMvxViewModel, TaskCompletionSource<object?>>();

        public event EventHandler<IMvxNavigateEventArgs>? WillNavigate;

        public event EventHandler<IMvxNavigateEventArgs>? DidNavigate;

        public event EventHandler<IMvxNavigateEventArgs>? WillClose;

        public event EventHandler<IMvxNavigateEventArgs>? DidClose;

        public event EventHandler<ChangePresentationEventArgs>? WillChangePresentation;

        public event EventHandler<ChangePresentationEventArgs>? DidChangePresentation;

        public MvxNavigationService(
            IMvxViewModelLoader viewModelLoader,
            IMvxViewDispatcher viewDispatcher,
            IMvxIoCProvider iocProvider)
        {
            _iocProvider = iocProvider;

            ViewModelLoader = viewModelLoader;
            ViewDispatcher = viewDispatcher;
            ViewsContainer = new Lazy<IMvxViewsContainer>(() => _iocProvider.Resolve<IMvxViewsContainer>());
        }

        public void LoadRoutes(IEnumerable<Assembly> assemblies)
        {
            if (assemblies == null)
                throw new ArgumentNullException(nameof(assemblies));

            Routes.Clear();
            foreach (var routeAttr in
                assemblies.SelectMany(a => a.GetCustomAttributes<MvxNavigationAttribute>()))
            {
                Routes.Add(new Regex(routeAttr.UriRegex,
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline),
                    routeAttr.ViewModelOrFacade);
            }
        }

        protected virtual bool TryGetRoute(string path, out KeyValuePair<Regex, Type> entry)
        {
            ValidateArguments(path);

            try
            {
                var matches = Routes.Where(t => t.Key.IsMatch(path)).ToList();

                switch (matches.Count)
                {
                    case 0:
                        entry = default;
                        _log.Value?.Log(LogLevel.Trace, "Unable to find routing for {path}", path);
                        return false;

                    case 1:
                        entry = matches[0];
                        return true;
                }

                var directMatch = matches.Where(t => t.Key.Match(path).Groups.Count == 1).ToList();

                if (directMatch.Count == 1)
                {
                    entry = directMatch[0];
                    return true;
                }

                _log.Value?.Log(LogLevel.Warning, "The following regular expressions match the provided url ({count}), each RegEx must be unique (otherwise try using IMvxRoutingFacade): {matches}",
                    matches.Count - 1,
                    string.Join(", ", matches.Select(t => t.Key.ToString())));

                // there is more than one match
                entry = default;
                return false;
            }
            catch (Exception ex)
            {
                _log.Value?.Log(LogLevel.Error, ex, "Unable to determine routability");
                entry = default;
                return false;
            }
        }

        protected virtual IDictionary<string, string> BuildParamDictionary(Regex regex, Match match)
        {
            if (regex == null)
                throw new ArgumentNullException(nameof(regex));

            if (match == null)
                throw new ArgumentNullException(nameof(match));

            var paramDict = new Dictionary<string, string>();

            for (var i = 1 /* 0 == Match itself */; i < match.Groups.Count; i++)
            {
                var group = match.Groups[i];
                var name = regex.GroupNameFromNumber(i);
                var value = group.Value;
                paramDict.Add(name, value);
            }
            return paramDict;
        }

        protected virtual async Task<MvxViewModelInstanceRequest> NavigationRouteRequest(
            string path, IMvxBundle? presentationBundle = null)
        {
            ValidateArguments(path);

            if (!TryGetRoute(path, out var entry))
            {
                throw new MvxException($"Navigation route request could not be obtained for path: {path}");
            }

            var regex = entry.Key;
            var match = regex.Match(path);
            var paramDict = BuildParamDictionary(regex, match);
            var parameterValues = new MvxBundle(paramDict);

            var viewModelType = entry.Value;

            var request = new MvxViewModelInstanceRequest(viewModelType)
            {
                PresentationValues = presentationBundle?.SafeGetData(),
                ParameterValues = parameterValues.SafeGetData()
            };

            if (viewModelType.GetInterfaces().Contains(typeof(IMvxNavigationFacade)))
            {
                var facade = (IMvxNavigationFacade)_iocProvider.IoCConstruct(viewModelType);

                try
                {
                    var facadeRequest = await facade.BuildViewModelRequest(path, paramDict).ConfigureAwait(false);
                    if (facadeRequest == null)
                    {
                        throw new MvxException($"{nameof(MvxNavigationService)}: Facade did not return a valid {nameof(MvxViewModelRequest)}.");
                    }

                    request.ViewModelType = facadeRequest.ViewModelType;

                    if (facadeRequest.ParameterValues != null)
                    {
                        request.ParameterValues = facadeRequest.ParameterValues;
                    }

                    if (facadeRequest is MvxViewModelInstanceRequest instanceRequest)
                    {
                        request.ViewModelInstance = instanceRequest.ViewModelInstance ?? ViewModelLoader.LoadViewModel(request, null);
                    }
                    else
                    {
                        request.ViewModelInstance = ViewModelLoader.LoadViewModel(request, null);
                    }
                }
                catch (Exception ex)
                {
                    throw ex.MvxWrap($"{nameof(MvxNavigationService)}: Exception thrown while processing URL: {path} with RoutingFacade: {viewModelType}");
                }
            }
            else
            {
                request.ViewModelInstance = ViewModelLoader.LoadViewModel(request, null);
            }

            return request;
        }

        protected async Task<MvxViewModelInstanceRequest> NavigationRouteRequest<TParameter>(
            string path, TParameter param, IMvxBundle? presentationBundle = null)
            where TParameter : notnull
        {
            ValidateArguments(path, param);

            if (!TryGetRoute(path, out var entry))
            {
                throw new MvxException($"Navigation route request could not be obtained for path: {path}");
            }

            var regex = entry.Key;
            var match = regex.Match(path);
            var paramDict = BuildParamDictionary(regex, match);
            var parameterValues = new MvxBundle(paramDict);

            var viewModelType = entry.Value;

            var request = new MvxViewModelInstanceRequest(viewModelType)
            {
                PresentationValues = presentationBundle?.SafeGetData(),
                ParameterValues = parameterValues.SafeGetData()
            };

            if (viewModelType.GetInterfaces().Contains(typeof(IMvxNavigationFacade)))
            {
                var facade = (IMvxNavigationFacade)_iocProvider.IoCConstruct(viewModelType);

                try
                {
                    var facadeRequest = await facade.BuildViewModelRequest(path, paramDict).ConfigureAwait(false);
                    if (facadeRequest == null)
                    {
                        throw new MvxException($"{nameof(MvxNavigationService)}: Facade did not return a valid {nameof(MvxViewModelRequest)}.");
                    }

                    request.ViewModelType = facadeRequest.ViewModelType;

                    if (facadeRequest.ParameterValues != null)
                    {
                        request.ParameterValues = facadeRequest.ParameterValues;
                    }

                    request.ViewModelInstance = ViewModelLoader.LoadViewModel(request, param, null);
                }
                catch (Exception ex)
                {
                    ex.MvxWrap($"{nameof(MvxNavigationService)}: Exception thrown while processing URL: {path} with RoutingFacade: {viewModelType}");
                }
            }
            else
            {
                request.ViewModelInstance = ViewModelLoader.LoadViewModel(request, param, null);
            }

            return request;
        }

        public virtual Task<bool> CanNavigate(string path)
        {
            return Task.FromResult(TryGetRoute(path, out _));
        }

        public virtual Task<bool> CanNavigate<TViewModel>()
            where TViewModel : IMvxViewModel
        {
            return Task.FromResult(ViewsContainer.Value.GetViewType(typeof(TViewModel)) != null);
        }

        public virtual Task<bool> CanNavigate(Type viewModelType)
        {
            return Task.FromResult(ViewsContainer.Value.GetViewType(viewModelType) != null);
        }

        protected virtual async Task<bool> Navigate(MvxViewModelRequest request, IMvxViewModel viewModel,
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
        {
            ValidateArguments(request, viewModel);

            var args = new MvxNavigateEventArgs(viewModel, NavigationMode.Show, cancellationToken);
            OnWillNavigate(this, args);

            if (args.Cancel)
                return false;

            var hasNavigated = await ViewDispatcher.ShowViewModel(request).ConfigureAwait(false);
            if (!hasNavigated)
                return false;

            if (viewModel.InitializeTask?.Task != null)
                await viewModel.InitializeTask.Task.ConfigureAwait(false);

            OnDidNavigate(this, args);
            return true;
        }

        protected virtual async Task<TResult?> Navigate<TResult>(MvxViewModelRequest request,
            IMvxViewModelResult<TResult> viewModel, IMvxBundle? presentationBundle = null,
            CancellationToken cancellationToken = default)
            where TResult : class
        {
            ValidateArguments(request, viewModel);

            var hasNavigated = false;
            var tcs = new TaskCompletionSource<object?>();

            if (cancellationToken != default)
            {
                cancellationToken.Register(() =>
                {
                    // ReSharper disable once AccessToModifiedClosure
                    if (hasNavigated && !tcs.Task.IsCompleted)
                        Task.Run(() => Close(viewModel, default, CancellationToken.None), CancellationToken.None);
                });
            }

            var args = new MvxNavigateEventArgs(viewModel, NavigationMode.Show, cancellationToken);
            OnWillNavigate(this, args);

            viewModel.CloseCompletionSource = tcs;
            TaskCompletionResults.Add(viewModel, tcs);

            cancellationToken.ThrowIfCancellationRequested();

            hasNavigated = await ViewDispatcher.ShowViewModel(request).ConfigureAwait(false);
            if (!hasNavigated)
                return default;

            if (viewModel.InitializeTask?.Task != null)
                await viewModel.InitializeTask.Task.ConfigureAwait(false);

            OnDidNavigate(this, args);

            try
            {
                return (TResult?)await tcs.Task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return default;
            }
        }

        protected virtual async Task<TResult?> Navigate<TParameter, TResult>(MvxViewModelRequest request,
            IMvxViewModel<TParameter, TResult> viewModel, TParameter param, IMvxBundle? presentationBundle = null,
            IMvxNavigateEventArgs? args = null, CancellationToken cancellationToken = default)
            where TParameter : notnull
            where TResult : class
        {
            ValidateArguments(request, viewModel, param);

            var hasNavigated = false;
            if (cancellationToken != default)
            {
                cancellationToken.Register(() =>
                {
                    // ReSharper disable once AccessToModifiedClosure
                    if (hasNavigated)
                        Task.Run(() => Close(viewModel, default, CancellationToken.None), CancellationToken.None);
                });
            }

            args ??= new MvxNavigateEventArgs(viewModel, NavigationMode.Show, cancellationToken);

            OnWillNavigate(this, args);

            var tcs = new TaskCompletionSource<object?>();
            viewModel.CloseCompletionSource = tcs;
            TaskCompletionResults.Add(viewModel, tcs);

            cancellationToken.ThrowIfCancellationRequested();

            hasNavigated = await ViewDispatcher.ShowViewModel(request).ConfigureAwait(false);

            if (viewModel.InitializeTask?.Task != null)
                await viewModel.InitializeTask.Task.ConfigureAwait(false);

            OnDidNavigate(this, args);

            try
            {
                return (TResult?)await tcs.Task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return default;
            }
        }

        public virtual async Task<bool> Navigate(
            string path, IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
        {
            var request = await NavigationRouteRequest(path, presentationBundle).ConfigureAwait(false);
            if (request.ViewModelInstance == null)
            {
                _log.Value?.Log(LogLevel.Warning, "Navigation Route Request doesn't have a ViewModelInstance");
                return false;
            }

            return await Navigate(request, request.ViewModelInstance, presentationBundle, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<bool> Navigate<TParameter>(string path, TParameter param,
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TParameter : notnull
        {
            var request = await NavigationRouteRequest(path, param, presentationBundle).ConfigureAwait(false);
            if (request.ViewModelInstance == null)
            {
                _log.Value?.Log(LogLevel.Warning, "Navigation Route Request doesn't have a ViewModelInstance");
                return false;
            }
            return await Navigate(request, request.ViewModelInstance, presentationBundle, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<TResult?> Navigate<TResult>(string path, IMvxBundle? presentationBundle = null,
            CancellationToken cancellationToken = default)
            where TResult : class
        {
            var request = await NavigationRouteRequest(path, presentationBundle).ConfigureAwait(false);
            if (request.ViewModelInstance == null)
            {
                _log.Value?.Log(LogLevel.Warning, "Navigation Route Request doesn't have a ViewModelInstance");
                return null;
            }
            return await Navigate(request, (IMvxViewModelResult<TResult>)request.ViewModelInstance, presentationBundle, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<TResult?> Navigate<TParameter, TResult>(string path, TParameter param,
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TParameter : notnull
            where TResult : class
        {
            var request = await NavigationRouteRequest(path, param, presentationBundle).ConfigureAwait(false);
            if (request.ViewModelInstance == null)
            {
                _log.Value?.Log(LogLevel.Warning, "Navigation Route Request doesn't have a ViewModelInstance");
                return null;
            }
            return await Navigate(request, (IMvxViewModel<TParameter, TResult>)request.ViewModelInstance, param, presentationBundle, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public virtual Task<bool> Navigate(Type viewModelType, IMvxBundle? presentationBundle = null,
            CancellationToken cancellationToken = default)
        {
            var request = new MvxViewModelInstanceRequest(viewModelType)
            {
                PresentationValues = presentationBundle?.SafeGetData()
            };
            request.ViewModelInstance = ViewModelLoader.LoadViewModel(request, null);
            return Navigate(request, request.ViewModelInstance, presentationBundle, cancellationToken);
        }

        public virtual Task<bool> Navigate<TParameter>(Type viewModelType, TParameter param,
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TParameter : notnull
        {
            var request = new MvxViewModelInstanceRequest(viewModelType)
            {
                PresentationValues = presentationBundle?.SafeGetData()
            };
            request.ViewModelInstance = ViewModelLoader.LoadViewModel(request, param, null);
            return Navigate(request, request.ViewModelInstance, presentationBundle, cancellationToken);
        }

        public virtual Task<TResult?> Navigate<TResult>(Type viewModelType, IMvxBundle? presentationBundle = null,
            CancellationToken cancellationToken = default)
            where TResult : class
        {
            var request = new MvxViewModelInstanceRequest(viewModelType)
            {
                PresentationValues = presentationBundle?.SafeGetData()
            };
            request.ViewModelInstance = (IMvxViewModelResult<TResult>)ViewModelLoader.LoadViewModel(request, null);
            return Navigate(request, (IMvxViewModelResult<TResult>)request.ViewModelInstance, presentationBundle, cancellationToken);
        }

        public virtual Task<TResult?> Navigate<TParameter, TResult>(Type viewModelType, TParameter param,
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TParameter : notnull
            where TResult : class
        {
            var args = new MvxNavigateEventArgs(NavigationMode.Show, cancellationToken);
            var request = new MvxViewModelInstanceRequest(viewModelType)
            {
                PresentationValues = presentationBundle?.SafeGetData()
            };
            request.ViewModelInstance = (IMvxViewModel<TParameter, TResult>)ViewModelLoader.LoadViewModel(request, param, null);
            args.ViewModel = request.ViewModelInstance;
            return Navigate(request, (IMvxViewModel<TParameter, TResult>)request.ViewModelInstance, param, presentationBundle, args, cancellationToken);
        }

        public virtual Task<bool> Navigate<TViewModel>(
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TViewModel : IMvxViewModel
        {
            return Navigate(typeof(TViewModel), presentationBundle, cancellationToken);
        }

        public virtual Task<bool> Navigate<TViewModel, TParameter>(
            TParameter param, IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TViewModel : IMvxViewModel<TParameter>
            where TParameter : notnull
        {
            return Navigate(typeof(TViewModel), param, presentationBundle, cancellationToken);
        }

        public virtual Task<TResult?> Navigate<TViewModel, TResult>(
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TViewModel : IMvxViewModelResult<TResult>
            where TResult : class
        {
            return Navigate<TResult>(typeof(TViewModel), presentationBundle, cancellationToken);
        }

        public virtual Task<TResult?> Navigate<TViewModel, TParameter, TResult>(
            TParameter param, IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TViewModel : IMvxViewModel<TParameter, TResult>
            where TParameter : notnull
            where TResult : class
        {
            return Navigate<TParameter, TResult>(typeof(TViewModel), param, presentationBundle, cancellationToken);
        }

        public virtual Task<bool> Navigate(
            IMvxViewModel viewModel, IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
        {
            var request = new MvxViewModelInstanceRequest(viewModel) { PresentationValues = presentationBundle?.SafeGetData() };
            ViewModelLoader.ReloadViewModel(viewModel, request, null);
            return Navigate(request, viewModel, presentationBundle, cancellationToken);
        }

        public virtual Task<bool> Navigate<TParameter>(IMvxViewModel<TParameter> viewModel, TParameter param,
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TParameter : notnull
        {
            var request = new MvxViewModelInstanceRequest(viewModel) { PresentationValues = presentationBundle?.SafeGetData() };
            ViewModelLoader.ReloadViewModel(viewModel, param, request, null);
            return Navigate(request, viewModel, presentationBundle, cancellationToken);
        }

        public virtual Task<TResult?> Navigate<TResult>(IMvxViewModelResult<TResult> viewModel,
            IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TResult : class
        {
            var request = new MvxViewModelInstanceRequest(viewModel) { PresentationValues = presentationBundle?.SafeGetData() };
            ViewModelLoader.ReloadViewModel(viewModel, request, null);
            return Navigate(request, viewModel, presentationBundle, cancellationToken);
        }

        public virtual Task<TResult?> Navigate<TParameter, TResult>(IMvxViewModel<TParameter, TResult> viewModel,
            TParameter param, IMvxBundle? presentationBundle = null, CancellationToken cancellationToken = default)
            where TParameter : notnull
            where TResult : class
        {
            var args = new MvxNavigateEventArgs(viewModel, NavigationMode.Show, cancellationToken);
            var request = new MvxViewModelInstanceRequest(viewModel) { PresentationValues = presentationBundle?.SafeGetData() };
            ViewModelLoader.ReloadViewModel(viewModel, param, request, null);
            return Navigate(request, viewModel, param, presentationBundle, args, cancellationToken);
        }

        public virtual async Task<bool> ChangePresentation(
            MvxPresentationHint hint, CancellationToken cancellationToken = default)
        {
            ValidateArguments(hint);

            _log.Value?.Log(LogLevel.Trace, "Requesting presentation change");
            var args = new ChangePresentationEventArgs(hint, cancellationToken);
            OnWillChangePresentation(this, args);

            if (args.Cancel)
                return false;

            var result = await ViewDispatcher.ChangePresentation(hint).ConfigureAwait(false);

            args.Result = result;
            OnDidChangePresentation(this, args);

            return result;
        }

        public virtual async Task<bool> Close(IMvxViewModel viewModel, CancellationToken cancellationToken = default)
        {
            if (viewModel is ITransactionViewModel tr && tr.TransactionRequesterId != null)
            {
                CancelTransaction(tr);
            }

            ValidateArguments(viewModel);

            var args = new MvxNavigateEventArgs(viewModel, NavigationMode.Close, cancellationToken);
            OnWillClose(this, args);

            if (args.Cancel)
                return false;

            var close = await ViewDispatcher.ChangePresentation(new MvxClosePresentationHint(viewModel)).ConfigureAwait(false);
            OnDidClose(this, args);

            return close;
        }

        public virtual async Task<bool> Close<TResult>(
            IMvxViewModelResult<TResult> viewModel, TResult? result, CancellationToken cancellationToken = default)
            where TResult : class
        {
            ValidateArguments(viewModel);

            TaskCompletionResults.TryGetValue(viewModel, out var tcs);

            //Disable cancelation of the Task when closing ViewModel through the service
            viewModel.CloseCompletionSource = null;

            try
            {
                var closeResult = await Close(viewModel, cancellationToken).ConfigureAwait(false);
                if (closeResult)
                {
                    tcs?.TrySetResult(result);
                    TaskCompletionResults.Remove(viewModel);
                }
                else
                    viewModel.CloseCompletionSource = tcs;
                return closeResult;
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
                return false;
            }
        }

        protected virtual void OnWillNavigate(object sender, IMvxNavigateEventArgs e)
        {
            WillNavigate?.Invoke(sender, e);
        }

        protected virtual void OnDidNavigate(object sender, IMvxNavigateEventArgs e)
        {
            DidNavigate?.Invoke(sender, e);
        }

        protected virtual void OnWillClose(object sender, IMvxNavigateEventArgs e)
        {
            WillClose?.Invoke(sender, e);
        }

        protected virtual void OnDidClose(object sender, IMvxNavigateEventArgs e)
        {
            DidClose?.Invoke(sender, e);
        }

        protected virtual void OnWillChangePresentation(object sender, ChangePresentationEventArgs e)
        {
            WillChangePresentation?.Invoke(sender, e);
        }

        protected virtual void OnDidChangePresentation(object sender, ChangePresentationEventArgs e)
        {
            DidChangePresentation?.Invoke(sender, e);
        }

        private static void ValidateArguments<TParameter>(MvxViewModelRequest request, IMvxViewModel viewModel, TParameter param)
            where TParameter : notnull
        {
            ValidateArguments(request, viewModel);

            if (param == null)
                throw new ArgumentNullException(nameof(param));
        }

        private static void ValidateArguments(MvxViewModelRequest request, IMvxViewModel viewModel)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (viewModel == null)
                throw new ArgumentNullException(nameof(viewModel));
        }

        private static void ValidateArguments<TParameter>(string path, TParameter param) where TParameter : notnull
        {
            ValidateArguments(path);

            if (param == null)
                throw new ArgumentNullException(nameof(param));
        }

        private static void ValidateArguments(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
        }

        private static void ValidateArguments(MvxPresentationHint hint)
        {
            if (hint == null)
                throw new ArgumentNullException(nameof(hint));
        }

        private static void ValidateArguments(IMvxViewModel viewModel)
        {
            if (viewModel == null)
                throw new ArgumentNullException(nameof(viewModel));
        }

        private static void ValidateArguments<TResult>(IMvxViewModelResult<TResult> viewModel) where TResult : notnull
        {
            if (viewModel == null)
                throw new ArgumentNullException(nameof(viewModel));
        }

        /// <summary>
        /// Bundle key used for the transaction ID
        /// </summary>
        public const string TransactionRequestIdKey = "_tqId";

        /// <summary>
        /// Bundle key used for response transaction ID
        /// </summary>
        public const string TransactionResponderIdKey = "_trId";

        /// <summary>
        /// Maps transaction IDs to their receivers. If a transaction result is set and
        /// the receiver in this map is still alive 
        /// </summary>
        private readonly Dictionary<string, WeakReference<ITransactionRequesterViewModel>> _transactionMap =
            new Dictionary<string, WeakReference<ITransactionRequesterViewModel>>();

        /// <summary>
        /// Intermediary result map for when the result cannot be delivered immediately
        /// </summary>
        private readonly Dictionary<string, Tuple<Type, bool, object>> _resultMap = new Dictionary<string, Tuple<Type, bool, object>>();

        /// <summary>
        /// Generates a view model transaction ID
        /// </summary>
        /// <returns></returns>
        private static string GenerateTransactionId()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Generates a transaction ID and logs the parent in the table.
        /// </summary>
        /// <param name="parent"></param>
        /// <param name="child"></param>
        private void GenerateTransaction(ITransactionRequesterViewModel parent, ITransactionResponderViewModel child)
        {
            var transactionId = GenerateTransactionId();
            parent.TransactionRequesterId = transactionId;
            child.TransactionResponderId = transactionId;

            _transactionMap[transactionId] = new WeakReference<ITransactionRequesterViewModel>(parent);
        }

        ///
        /// <inheritdoc />
        ///
        public Task NavigateForResult<TViewModel, TResult>(ITransactionRequesterViewModel<TResult> receiver)
            where TViewModel : ITransactionResponderViewModel
        {
            if (receiver == null) throw new ArgumentNullException(nameof(receiver));

            var request = new MvxViewModelInstanceRequest(typeof(TViewModel));
            var viewModel = ViewModelLoader.LoadViewModel(request, null);
            GenerateTransaction(receiver, (ITransactionViewModel)viewModel);

            request.ViewModelInstance = viewModel;
            return Navigate(request, viewModel);
        }

        ///
        /// <inheritdoc />
        ///
        public Task NavigateForResult<TViewModel, TParameter, TResult>(ITransactionRequesterViewModel<TResult> receiver,
            TParameter parameter)
            where TViewModel : ITransactionResponderViewModel, IMvxViewModel<TParameter>
        {
            if (receiver == null) throw new ArgumentNullException(nameof(receiver));

            var request = new MvxViewModelInstanceRequest(typeof(TViewModel));
            var vm = (IMvxViewModel<TParameter>)ViewModelLoader.LoadViewModel(request, parameter, null);
            GenerateTransaction(receiver, (ITransactionViewModel)vm);

            request.ViewModelInstance = vm;
            return Navigate(request, vm);
        }

        ///
        /// <inheritdoc />
        ///
        public Task CloseWithResult<TResult>(ITransactionResponderViewModel viewModel, TResult result)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));

            var transactionId = viewModel.TransactionResponderId;
            viewModel.TransactionResponderId = null;

            var hasValue = _transactionMap.TryGetValue(transactionId, out var vmReference);
            if (hasValue)
            {
                _transactionMap.Remove(transactionId);
            }

            // Check if the weak reference has not expired and the transaction ID is still valid
            if (hasValue && vmReference.TryGetTarget(out var target) && target.TransactionRequesterId == transactionId && target.CanReceiveResult())
            {
                if (!(target is ITransactionRequesterViewModel<TResult> tTarget))
                {
                    throw new NotSupportedException($"Trying to set a result of type {typeof(TResult)} on view model" +
                                                    $" of type {target}, which doesn't implement TRequestViewModel<{typeof(TResult)}>");
                }

                // Set the result directly
                tTarget.OnResult(result);
                tTarget.TransactionRequesterId = null;
                return Close(viewModel);
            }

            // Store the result in the result table, and let the parent view model retrieve it
            // when it is rehydrated.
            _resultMap[transactionId] = new Tuple<Type, bool, object>(typeof(TResult), true, result);
            return Close(viewModel);
        }

        /// <summary>
        /// Cancels an ongoing transaction from a sending view model
        /// </summary>
        /// <param name="tr"></param>
        public void CancelTransaction(ITransactionViewModel tr)
        {
            if (tr == null) throw new ArgumentNullException(nameof(tr));

            if (tr.TransactionRequesterId == null) return;

            var transactionId = tr.TransactionRequesterId;
            tr.TransactionRequesterId = null;

            if (!_transactionMap.TryGetValue(transactionId, out var vmReference)) return;
            _transactionMap.Remove(transactionId);

            if (vmReference.TryGetTarget(out var target) && target.CanReceiveResult())
            {
                // Cancel the transaction directly on the view model
                target.TransactionRequesterId = null;
            }
            else
            {
                // Set a negative transaction result for the new / rehydrated view model
                // to pick up.
                _resultMap.Add(transactionId, new Tuple<Type, bool, object>(null, false, null));
            }
        }

        ///
        /// <inheritdoc />
        ///
        public bool ObtainResult(ITransactionRequesterViewModel viewModel)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));

            if (!viewModel.CanReceiveResult())
            {
                _log.Value?.Log(LogLevel.Warning, $"ObtainResult called for a viewmodel of type {viewModel.GetType()} in a non-susceptible transaction state. ");
                return false;
            }

            // Check if there is a transaction for this view model, and whether there is
            // a result available for it.
            var transactionId = viewModel.TransactionRequesterId;
            if (transactionId == null || !_resultMap.TryGetValue(transactionId, out var resultTuple)) return false;

            // Only clear the transaction ID if there is a result. For one thing, this view model
            // may be a transaction sender and not receiver, and this method may also be called
            // too early in the lifetime. This does mean that IDs for unfinished transactions
            // might linger, but this would seem harmless to me.
            viewModel.TransactionRequesterId = null;
            _resultMap.Remove(transactionId);

            if (!resultTuple.Item2)
            {
                // Result was cancelled
                return true;
            }

            var type = resultTuple.Item1;
            var obj = resultTuple.Item3;

            // Find the result method on the view model
            const string onResultName = nameof(ITransactionRequesterViewModel<object>.OnResult);
            var resultMethod = viewModel.GetType().GetMethods()
                .Where(m =>
                {
                    var name = m.Name;
                    var parameters = m.GetParameters();
                    return name == onResultName && parameters.Length == 1 &&
                           parameters[0].ParameterType == type;
                })
                .FirstOrDefault();

            if (resultMethod == null)
            {
                throw new NotSupportedException($"Trying to set a transaction result of type ${type}" +
                                                $" on a view model of type ${viewModel.GetType()}, but" +
                                                $" the view model has no method {onResultName}({type})");
            }

            // Invoke the result method
            resultMethod.Invoke(viewModel, new[] { obj });
            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="viewModel"></param>
        /// <param name="bundle"></param>
#pragma warning disable CA1822 // Mark members as static
        public void SaveTransactionState(ITransactionRequesterViewModel viewModel, IMvxBundle bundle)
#pragma warning restore CA1822 // Mark members as static
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));

            bundle.Data.Add(TransactionRequestIdKey, viewModel.TransactionRequesterId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="viewModel"></param>
        /// <param name="bundle"></param>
#pragma warning disable CA1822 // Mark members as static
        public void SaveTransactionState(ITransactionResponderViewModel viewModel, IMvxBundle bundle)
#pragma warning restore CA1822 // Mark members as static
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));

            bundle.Data.Add(TransactionResponderIdKey, viewModel.TransactionResponderId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="viewModel"></param>
        /// <param name="bundle"></param>
        public void SaveTransactionState(ITransactionViewModel viewModel, IMvxBundle bundle)
        {
            SaveTransactionState((ITransactionRequesterViewModel)viewModel, bundle);
            SaveTransactionState((ITransactionResponderViewModel)viewModel, bundle);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="viewModel"></param>
        /// <param name="bundle"></param>
#pragma warning disable CA1822 // Mark members as static
        public void RestoreTransactionState(ITransactionRequesterViewModel viewModel, IMvxBundle bundle)
#pragma warning restore CA1822 // Mark members as static
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));

            if (bundle?.Data == null) return;
            if (bundle.Data.TryGetValue(TransactionRequestIdKey, out var value))
            {
                viewModel.TransactionRequesterId = value;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="viewModel"></param>
        /// <param name="bundle"></param>
#pragma warning disable CA1822 // Mark members as static
        public void RestoreTransactionState(ITransactionResponderViewModel viewModel, IMvxBundle bundle)
#pragma warning restore CA1822 // Mark members as static
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));

            if (bundle?.Data == null) return;
            if (bundle.Data.TryGetValue(TransactionResponderIdKey, out var value))
            {
                viewModel.TransactionResponderId = value;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="viewModel"></param>
        /// <param name="bundle"></param>
        public void RestoreTransactionState(ITransactionViewModel viewModel, IMvxBundle bundle)
        {
            RestoreTransactionState((ITransactionRequesterViewModel)viewModel, bundle);
            RestoreTransactionState((ITransactionResponderViewModel)viewModel, bundle);
        }
    }
#nullable restore
}
