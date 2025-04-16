using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WorkflowCore.Primitives;

namespace WorkflowCore.Services
{
    public class CancellationProcessor : ICancellationProcessor
    {
        protected readonly ILogger _logger;
        private readonly IExecutionResultProcessor _executionResultProcessor;
        private readonly IDateTimeProvider _dateTimeProvider;
        private readonly IPersistenceProvider _persistenceProvider;

        public CancellationProcessor(
            IExecutionResultProcessor executionResultProcessor, 
            ILoggerFactory logFactory, 
            IDateTimeProvider dateTimeProvider,
            IPersistenceProvider persistenceProvider)
        {
            _executionResultProcessor = executionResultProcessor;
            _logger = logFactory.CreateLogger<CancellationProcessor>();
            _dateTimeProvider = dateTimeProvider;
            _persistenceProvider = persistenceProvider;
        }

        public void ProcessCancellations(WorkflowInstance workflow, WorkflowDefinition workflowDef, WorkflowExecutorResult executionResult)
        {
            foreach (var step in workflowDef.Steps.Where(x => x.CancelCondition != null))
            {
                var func = step.CancelCondition.Compile();
                var cancel = false;
                try
                {
                    cancel = (bool)(func.DynamicInvoke(workflow.Data));
                }
                catch (Exception ex)
                {
                    _logger.LogError(default(EventId), ex, ex.Message);
                }
                if (cancel)
                {
                    var toCancel = workflow.ExecutionPointers.Where(x => x.StepId == step.Id && x.Status != PointerStatus.Complete && x.Status != PointerStatus.Cancelled).ToList();

                    // Clean up ScheduledCommands for the workflow once, before processing cancellations
                    if (_persistenceProvider.SupportsScheduledCommands && toCancel.Any())
                    {
                        _persistenceProvider.ProcessCommands(DateTimeOffset.MaxValue, async (cmd) => {
                            if (cmd.CommandName == ScheduledCommand.ProcessWorkflow && cmd.Data == workflow.Id)
                            {
                                // Command will be automatically removed after processing
                                return;
                            }
                        }).Wait();
                    }

                    foreach (var ptr in toCancel)
                    {
                        if (step.ProceedOnCancel)
                        {
                            _executionResultProcessor.ProcessExecutionResult(workflow, workflowDef, ptr, step, ExecutionResult.Next(), executionResult);
                        }

                        ptr.EndTime = _dateTimeProvider.UtcNow;
                        ptr.Active = false;
                        ptr.Status = PointerStatus.Cancelled;

                        foreach (var descendent in workflow.ExecutionPointers.FindByScope(ptr.Id).Where(x => x.Status != PointerStatus.Complete && x.Status != PointerStatus.Cancelled))
                        {
                            descendent.EndTime = _dateTimeProvider.UtcNow;
                            descendent.Active = false;
                            descendent.Status = PointerStatus.Cancelled;
                        }
                    }
                }
            }
        }
    }
}
