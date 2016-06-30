﻿#region

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autofac;
using MultiThreadedReactiveUI.DataProvider;
using MultiThreadedReactiveUI.Extensions;
using MultiThreadedReactiveUI.Model;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

#endregion

namespace MultiThreadedReactiveUI.ViewModel
{

    public class StocksViewModel : ReactiveObject, IStocksViewModel
    {

        private readonly IStocksDataProvider _DataProvider;
        int CurrentLoopCounter;

        public StocksViewModel(IStocksDataProvider dataProvider, IContainer container)
        {

            _DataProvider = dataProvider;
            Container = container;
            CancellationTokenSource = new CancellationTokenSource();
            SelectedStocks = new ReactiveList<Stock>();
            TradesToExecute = new ReactiveList<StockTradeExecutionTaskViewModel>();
            TradesExecuted = new ReactiveList<StockTradeExecutionTaskViewModel>();
            TradesExecutedValues = new Subject<StockTradeExecutionTaskViewModel>();
            TradesExecutedValuesBuffer = new Subject<StockTradeExecutionTaskViewModel>();
            
            TradesExecutedValues.Subscribe(TradeExecuted, TradeError, TradeCompleted);

            ResetStocksData();



            StartExecutingTrades = ReactiveCommand.CreateAsyncTask<AsyncVoid>(_ => { return RunTradesToExecuteAsync(); });

            AddStockToTradesToExecute = ReactiveCommand.CreateAsyncTask<List<StockTradeExecutionTaskViewModel>>(_ => { return AddStockToStocksToExecuteAsync(); });
            AddStockToTradesToExecute
                    .Subscribe(p =>
                    {
                        using (TradesToExecute.SuppressChangeNotifications())
                        {
                            TradesToExecute.Clear();
                            TradesToExecute.AddRange(p);
                            SelectedStockToTrade = TradesToExecute.LastOrDefault();
                        }
                    });
            AddStockToTradesToExecute.ThrownExceptions.Subscribe(
                ex => Console.WriteLine("Error whilst adding Stocks! Err: {0}", ex.Message));

            RemoveStockFromStocksToExecute = ReactiveCommand.CreateAsyncTask<List<StockTradeExecutionTaskViewModel>>(_ => { return RemoveStockFromStocksToExecuteAsync(); });
            RemoveStockFromStocksToExecute
                    .Subscribe(p =>
                    {
                        using (TradesToExecute.SuppressChangeNotifications())
                        {
                            TradesToExecute.Clear();
                            TradesToExecute.AddRange(p);
                            SelectedStockToTrade = TradesToExecute.FirstOrDefault();
                        }
                    });
            RemoveStockFromStocksToExecute.ThrownExceptions.Subscribe(
                ex => Console.WriteLine("Error whilst removing Stocks! Err: {0}", ex.Message));

            RunStocksToExecute = ReactiveCommand.CreateAsyncTask<AsyncVoid>(_ => { return RunTradesToExecuteAsync(); });
            RunStocksToExecute.ThrownExceptions.Subscribe(
                ex => Console.WriteLine("Error whilst running Stocks! Err: {0}", ex.Message));


            CancelRunningStocksToExecute = ReactiveCommand.CreateAsyncTask<AsyncVoid>(_ => { return CancelRunningStocksToExecuteAsync(); });
            CancelRunningStocksToExecute.ThrownExceptions.Subscribe(
                ex => Console.WriteLine("Error whilst cancelling running Stocks! Err: {0}", ex.Message));

            SetupCancelRunViewModels();
            SetComputationViewModelBusyIndicator(false);

        }

        private Task<List<StockTradeExecutionTaskViewModel>> AddStockToStocksToExecuteAsync()
        {
            return Task.Run(() =>
            {
                List<StockTradeExecutionTaskViewModel> updatedList = TradesToExecute.ToList();
                foreach (var Stock in SelectedStocks)
                {
                    StockTradeExecutionTaskViewModel viewModel = new StockTradeExecutionTaskViewModel();
                    viewModel.Symbol = Stock.Symbol;
                    viewModel.Price = Stock.Price;
                    viewModel.Quantity = 10;
                    viewModel.TradeType = TradeType.Buy;
                    updatedList.Add(viewModel);
                }

                return updatedList;
            });
        }



        private StockTradeExecutionTaskViewModel Average(IList<StockTradeExecutionTaskViewModel> arg)
        {
            int i = 0;
            var last = arg.Last();

            arg.Aggregate(arg.First(), (a, s) =>
            {
                s.AveragePrice = (a.AveragePrice * i + s.AveragePrice) / (i + 1);
                ++i;
                return s;
            });

            return last;
        }




        private Task<AsyncVoid> CancelRunningStocksToExecuteAsync()
        {
            return Task.Run(() =>
            {

                try
                {
                    CancellationTokenSource.Cancel();
                }
                catch (OperationCanceledException e)
                {
                    Console.WriteLine(e.Message);
                }
                finally
                {
                    ResetStockTradeExecutionTaskViewModel();
                    ToggleRunCancelCommand();
                    SetComputationViewModelBusyIndicator(false);
                    CancellationTokenSource.Dispose();
                }
                return AsyncVoid.Default;
            });
        }


        private Task<List<StockTradeExecutionTaskViewModel>> RemoveStockFromStocksToExecuteAsync()
        {
            return Task.Run(() =>
            {
                List<StockTradeExecutionTaskViewModel> updatedList = TradesToExecute.ToList();
                updatedList.Remove(SelectedStockToTrade);
                return updatedList;
            });
        }


        private void ResetStocksData()
        {
            var inMemoryStocks = _DataProvider.LoadStocks();
            Stocks = null;
            Stocks = new ReactiveList<Stock>();
            Stocks.Clear();



            foreach (var Stock in inMemoryStocks)
                Stocks.Add(Stock);
        }

        private void ResetStockTradeExecutionTaskViewModel()
        {
            Application.Current.Dispatcher.InvokeIfRequired(() =>
             {
                 foreach (var StockTradeExecutionTaskViewModel in TradesToExecute)
                 {
                     StockTradeExecutionTaskViewModel.IsIndeterminate = false;
                     StockTradeExecutionTaskViewModel.Progress = 0;
                 }
             }, Container);
        }

        /// <summary>
        /// This method will set UI on all tasks to indeterminate and then iterate through all the work to be done.
        /// </summary>
        /// <returns></returns>
        private Task<AsyncVoid> RunTradesToExecuteAsync()
        {
            ResetStockTradeExecutionTaskViewModel();
            SetProgress(0);
            CurrentLoopCounter = 0;
            ToggleRunCancelCommand();
            //Calculate total iterations to perform at start of work
            TotalIterationsForAllTasks = TradesToExecute.Count;
            SetComputationViewModelBusyIndicator(true);
            return Task.Run(() =>
            {
                RunTradesToExecuteInSeperateThreads();

                return AsyncVoid.Default;

            });
        }

        private void RunTradesToExecuteInSeperateThreads()
        {
            CancellationTokenSource = new CancellationTokenSource();
            ParallelOptions po = new ParallelOptions();
            po.CancellationToken = CancellationTokenSource.Token;
            po.MaxDegreeOfParallelism = System.Environment.ProcessorCount;

            TradesExecutedValuesBuffer.Buffer(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)).Subscribe(TradeBufferOnNext, TradeBufferOnError, TradeBufferOnCompleted);


            ConcurrentDictionary<int, StockTradeExecutionTaskViewModel> tasks = new ConcurrentDictionary<int, StockTradeExecutionTaskViewModel>();
            for (int i = 0; i < TradesToExecute.Count; i++)
                tasks.TryAdd(i, TradesToExecute[i]);
            TotalIterationsForAllTasks = tasks.Count;
            try
            {
                Parallel.ForEach(tasks, po, StockTradeExecutionTaskViewModel =>
                {
                    SimulateTrades(StockTradeExecutionTaskViewModel);
                    UpdateTradesExecuted(StockTradeExecutionTaskViewModel.Value);
                    TradesExecutedValuesBuffer.OnNext(StockTradeExecutionTaskViewModel.Value);

                    int percentCompleteAllTasks = (int)Math.Round((double)(100 * CurrentLoopCounter) / TotalIterationsForAllTasks);
                    SetProgress(percentCompleteAllTasks);
                    Interlocked.Increment(ref this.CurrentLoopCounter);
                    if (CurrentLoopCounter == TotalIterationsForAllTasks)
                    {
                        SetComputationViewModelBusyIndicator(false);
                        SetProgress(100);
                        ToggleRunCancelCommand();
                    }

                });
            }
            catch (OperationCanceledException e)
            {
                Console.WriteLine(e.Message);
            }
            finally
            {
                ComputeStockStatistics();
                TradesExecutedValues.OnCompleted();
            }

        }


        private void SetComputationViewModelBusyIndicator(bool value)
        {
            Application.Current.Dispatcher.InvokeIfRequired(() =>
               {
                   if (value == false)
                       IsBusy = true;
                   if (value == true)
                       IsBusy = false;
                   foreach (var StockTradeExecutionTaskViewModel in TradesToExecute)
                       StockTradeExecutionTaskViewModel.IsIndeterminate = value;
               }, Container);

        }

        private void SetProgress(int percentComplete)
        {
            Application.Current.Dispatcher.InvokeIfRequired(() =>
               {
                   ProgressForAllTasks = percentComplete;
               }, Container);
        }

        private void SetupCancelRunViewModels()
        {
            CancelRunViewModel cancelViewModel = new CancelRunViewModel();
            cancelViewModel.DisplayText = Constants.CancelButtonDisplayText;
            cancelViewModel.ReactiveCommandToExecute = CancelRunningStocksToExecute;
            CancelRunViewModelCancel = cancelViewModel;
            CancelRunViewModel runViewModel = new CancelRunViewModel();
            runViewModel.DisplayText = Constants.RunButtonDisplayText;
            runViewModel.ReactiveCommandToExecute = RunStocksToExecute;
            CancelRunViewModelRun = runViewModel;
            CurrentCancelRunViewModel = runViewModel;
        }

        private void SimulateTrades(KeyValuePair<int, StockTradeExecutionTaskViewModel> stockTradeExecutionTaskViewModel)
        {
            int progress = 0;
            while (progress <= 100)
            {
                Interlocked.Increment(ref progress);
                //progress += 1;
                Debug.WriteLine(String.Format("Stock: {0} Progress: {1}", stockTradeExecutionTaskViewModel.Value.Symbol, progress));
                UpdateStockTradeExecutionTaskViewModel(stockTradeExecutionTaskViewModel, progress);
                Thread.Sleep(1);
            }

        }

        private void ToggleRunCancelCommand()
        {
            Application.Current.Dispatcher.InvokeIfRequired(() =>
            {
                bool hasToggled = false;
                if (CurrentCancelRunViewModel.DisplayText == Constants.RunButtonDisplayText)
                {
                    CurrentCancelRunViewModel = CancelRunViewModelCancel;
                    hasToggled = true;
                }
                if (CurrentCancelRunViewModel.DisplayText == Constants.CancelButtonDisplayText && hasToggled == false)
                    CurrentCancelRunViewModel = CancelRunViewModelRun;
            }, Container);
        }

        private void TradeBufferOnNext(IList<StockTradeExecutionTaskViewModel> viewModelList){
            var viewModel = viewModelList.FirstOrDefault();
            if (viewModel != null){
                Debug.WriteLine(String.Format("TradeBufferOnNext: {0} Price:{1} Quantity:{2}", viewModel.Symbol, viewModel.Price, viewModel.Quantity));
            }
        }

        private void TradeBufferOnError(Exception ex)
        {
            Debug.WriteLine(String.Format("TradeBufferOnError: {0}", ex.Message));
        }

        private void TradeBufferOnCompleted()
        {
            Debug.WriteLine(String.Format("Trade Completed"));
        }


        private void TradeCompleted()
        {
            Debug.WriteLine(String.Format("Trade Completed"));
        }

        private void TradeError(Exception ex)
        {
            Debug.WriteLine(String.Format("Trade Error: {0}", ex.Message));
        }

        private void TradeExecuted(StockTradeExecutionTaskViewModel viewModel)
        {
            Debug.WriteLine(String.Format("Traded: {0} Price:{1} Quantity:{2}", viewModel.Symbol, viewModel.Price, viewModel.Quantity));
            TradesExecutedValuesBuffer.OnNext(viewModel);
        }

        private void ComputeStockStatistics()
        {
            //Splitting a stream into buffers of 20 days, each buffer starting on the
            //next StockQuote instance.  Because we are
            //returning the last Stock Quote instance from the buffer
            //the first output of this observable would be the 20th instance of the StockQuote
            //with its averages calculated

            //var quotesWithMovingAverage = TradesExecutedValues.Where(q => q.Stock.Symbol == viewModel.Stock.Symbol).Buffer(TimeSpan.FromMinutes(15)).Select(list => Average(list));
            //var quotesWithMovingAverage = TradesExecutedValues.Where(q => q.Symbol == viewModel.Symbol).Buffer(10).Select(list => Average(list));

            //var sourceList = TradesExecutedValuesBuffer.ToList() as List<StockTradeExecutionTaskViewModel>;
            //foreach (var item in sourceList)
            //{
            //    Debug.WriteLine(String.Format("Buffered Stock: {0}", item.Symbol));
            //}
            //TradesExecutedValuesBuffer.Dump("Trades from Last Five Seconds");
            //var average = TradesExecutedValues.Average(x => x.Price);
            //TradesExecutedValues.Average(x => x.Price).Dump(nameof(Average));
            //Debug.WriteLine(String.Format("Computed Average Price of: {0}", average));

            //TradesExecutedValues.Window(10)
            //.Select(window => window.Average())
            //.SelectMany(averageSequence => averageSequence);
        }

        private void UpdateStockTradeExecutionTaskViewModel(KeyValuePair<int, StockTradeExecutionTaskViewModel> StockTradeExecutionTaskViewModel, int progress)
        {
            Application.Current.Dispatcher.InvokeIfRequired(() =>
             {
                 var viewModelToUpdate = TradesToExecute[StockTradeExecutionTaskViewModel.Key];
                 Debug.WriteLine(String.Format("Updating ViewModel for Stock: {0} Key: {1} Progress: {2}", viewModelToUpdate.Symbol, viewModelToUpdate.Timestamp, progress));
                 viewModelToUpdate.IsIndeterminate = false;
                 viewModelToUpdate.Progress = progress;

             }, Container);
        }

        private void UpdateTradesExecuted(StockTradeExecutionTaskViewModel executedTrade)
        {
            Application.Current.Dispatcher.InvokeIfRequired(() =>
             {
                 executedTrade.Timestamp = DateTime.Now;
                 TradesExecuted.Add(executedTrade);
                 TradesExecutedValues.OnNext(executedTrade);

             }, Container);
        }

        private CancellationTokenSource CancellationTokenSource { get; set; }




        public ReactiveCommand<List<StockTradeExecutionTaskViewModel>> AddStockToTradesToExecute { get; protected set; }
        public ReactiveCommand<AsyncVoid> CancelRunningStocksToExecute { get; protected set; }

        [Reactive]
        public CancelRunViewModel CancelRunViewModelCancel { get; set; }
        [Reactive]
        public CancelRunViewModel CancelRunViewModelRun { get; set; }
        public IContainer Container { get; set; }
        [Reactive]
        public CancelRunViewModel CurrentCancelRunViewModel { get; set; }
        [Reactive]
        public StockTradeExecutionTaskViewModel CurrentStockTradeExecutionTaskViewModel { get; set; }
        [Reactive]
        public StockTradeExecutionTaskViewModel CurrentStockTradeExecutionTaskViewModelIndex { get; set; }
        [Reactive]
        public string ExecutionLabel { get; set; }
        [Reactive]
        public bool IsBusy { get; set; }
        [Reactive]
        public int ProgressForAllTasks { get; set; }

        public ReactiveCommand<List<StockTradeExecutionTaskViewModel>> RemoveStockFromStocksToExecute { get; protected set; }
        public ReactiveCommand<AsyncVoid> RunStocksToExecute { get; protected set; }

        public ReactiveList<Stock> SelectedStocks { get; set; }
        [Reactive]
        public StockTradeExecutionTaskViewModel SelectedStockToTrade { get; set; }

        public ReactiveCommand<AsyncVoid> StartExecutingTrades { get; protected set; }

        [Reactive]
        public ReactiveList<Stock> Stocks { get; set; }

        public Dictionary<string, ReactiveCommand<AsyncVoid>> ToggleExecutionDictionary { get; set; }
        [Reactive]
        public int TotalIterationsForAllTasks { get; set; }
        public int TotalProgressCounter { get; private set; }

        [Reactive]
        public ReactiveList<StockTradeExecutionTaskViewModel> TradesExecuted { get; set; }

        public Subject<StockTradeExecutionTaskViewModel> TradesExecutedValues { get; set; }
        [Reactive]
        public ReactiveList<StockTradeExecutionTaskViewModel> TradesToExecute { get; set; }
        public Subject<StockTradeExecutionTaskViewModel> TradesExecutedValuesBuffer { get; set; }
    }
}