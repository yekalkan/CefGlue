using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xilium.CefGlue.BrowserProcess.Serialization;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace Xilium.CefGlue.BrowserProcess.ObjectBinding
{
    internal class JavascriptToNativeDispatcherRenderSide : INativeObjectRegistry
    {
        private static volatile int lastCallId;

        private readonly ConcurrentDictionary<string, ObjectRegistrationInfo> _registeredObjects = new ConcurrentDictionary<string, ObjectRegistrationInfo>();
        private readonly ConcurrentDictionary<int, PromiseHolder> _pendingCalls = new ConcurrentDictionary<int, PromiseHolder>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingBoundQueryTasks = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        
        public JavascriptToNativeDispatcherRenderSide(MessageDispatcher dispatcher)
        {
            dispatcher.RegisterMessageHandler(Messages.NativeObjectRegistrationRequest.Name, HandleNativeObjectRegistration);
            dispatcher.RegisterMessageHandler(Messages.NativeObjectUnregistrationRequest.Name, HandleNativeObjectUnregistration);
            dispatcher.RegisterMessageHandler(Messages.NativeObjectCallResult.Name, HandleNativeObjectCallResult);

            JavascriptHelper.Register(this);
        }

        private void HandleNativeObjectRegistration(MessageReceivedEventArgs args)
        {
            var message = Messages.NativeObjectRegistrationRequest.FromCefMessage(args.Message);
            var objectInfo = new ObjectRegistrationInfo(message.ObjectName, message.MethodsNames);

            if (_registeredObjects.TryAdd(objectInfo.Name, objectInfo))
            {
                // register objects in the main frame
                using (var frame = args.Browser.GetMainFrame())
                using (var context = frame.V8Context.EnterOrFail())
                {
                    var objectCreated = CreateNativeObjects(new[] { objectInfo }, context.V8Context);

                    if (objectCreated)
                    {
                        // notify that the object has been registered, any pending promises on the object will be resolved
                        var taskSource = _pendingBoundQueryTasks.GetOrAdd(objectInfo.Name, _ => new TaskCompletionSource<bool>());
                        taskSource.TrySetResult(true);
                    }
                }
            }
        }

        private void HandleNativeObjectUnregistration(MessageReceivedEventArgs args)
        {
            var message = Messages.NativeObjectUnregistrationRequest.FromCefMessage(args.Message);

            using (var frame = args.Browser.GetMainFrame()) // unregister objects from the main frame
            using (var context = frame.V8Context.EnterOrFail())
            {
                DeleteNativeObject(message.ObjectName, context.V8Context);
            }
        }

        private PromiseHolder HandleNativeObjectCall(Messages.NativeObjectCallRequest message)
        {
            message.CallId = lastCallId++;

            using (var context = CefV8Context.GetCurrentContext().EnterOrFail(shallDispose: false)) // context will be released when promise is resolved
            using (var frame = context.V8Context.GetFrame())
            {
                if (frame == null)
                {
                    // TODO, what now?
                    return null;
                }

                var promiseHolder = context.V8Context.CreatePromise();
                if (!_pendingCalls.TryAdd(message.CallId, promiseHolder))
                {
                    throw new InvalidOperationException("Call id already exists");
                }

                using (var cefMessage = message.ToCefProcessMessage())
                {
                    frame.SendProcessMessage(CefProcessId.Browser, cefMessage);
                }

                return promiseHolder;
            }
        }

        private void HandleNativeObjectCallResult(MessageReceivedEventArgs args)
        {
            var message = Messages.NativeObjectCallResult.FromCefMessage(args.Message);
            if (_pendingCalls.TryRemove(message.CallId, out var promiseHolder))
            {
                using (promiseHolder)
                using (var context = promiseHolder.Context.EnterOrFail())
                {
                    promiseHolder.ResolveOrReject((resolve, reject) =>
                    {
                        if (message.Success)
                        {
                            using (var value = V8ValueSerialization.SerializeCefValue(message.Result))
                            {
                                resolve(value);
                            }
                        }
                        else
                        {
                            using (var exceptionMsg = CefV8Value.CreateString(message.Exception))
                            {
                                reject(exceptionMsg);
                            }
                        }
                    });
                }
            }
        }

        public void HandleContextCreated(CefV8Context context, bool isMain)
        {
            if (isMain)
            {
                CreateNativeObjects(_registeredObjects.Values, context);
            }
        }

        public void HandleContextReleased(CefV8Context context, bool isMain)
        {
            void ReleasePromiseHolder(PromiseHolder promiseHolder)
            {
                promiseHolder.Context.Dispose();
                promiseHolder.Dispose();
            }

            if (isMain)
            {
                foreach (var promiseHolder in _pendingCalls.Values)
                {
                    ReleasePromiseHolder(promiseHolder);
                }
                _pendingCalls.Clear();
            }
            else
            {
                foreach (var promiseHolderEntry in _pendingCalls.ToArray())
                {
                    if (promiseHolderEntry.Value.Context.IsSame(context))
                    {
                        _pendingCalls.TryRemove(promiseHolderEntry.Key, out var dummy);
                        ReleasePromiseHolder(promiseHolderEntry.Value);
                    }
                }
            }
        }

        private bool CreateNativeObjects(IEnumerable<ObjectRegistrationInfo> objectInfos, CefV8Context context)
        {
            if (context.Enter())
            {
                try
                {
                    using (var global = context.GetGlobal())
                    {
                        foreach (var objectInfo in objectInfos)
                        {
                            var handler = new V8FunctionHandler(objectInfo.Name, HandleNativeObjectCall);
                            var attributes = CefV8PropertyAttribute.ReadOnly | CefV8PropertyAttribute.DontDelete;

                            using (var v8Obj = CefV8Value.CreateObject())
                            {
                                foreach (var methodName in objectInfo.MethodsNames)
                                {
                                    using (var v8Function = CefV8Value.CreateFunction(methodName, handler))
                                    {
                                        v8Obj.SetValue(methodName, v8Function, attributes);
                                    }
                                }

                                global.SetValue(objectInfo.Name, v8Obj);
                            }
                        }
                    }

                    return true;
                }
                finally
                {
                    context.Exit();
                }
            }
            else
            {
                // TODO
                return false;
            }
        }

        private void DeleteNativeObject(string objName, CefV8Context context)
        {
            if (_registeredObjects.TryRemove(objName, out var objectInfo))
            {
                using (var global = context.GetGlobal())
                {
                    global.DeleteValue(objectInfo.Name);
                }
            }
        }

        Task<bool> INativeObjectRegistry.Bind(string objName)
        {
            return _pendingBoundQueryTasks.GetOrAdd(objName, _ => new TaskCompletionSource<bool>()).Task;
        }

        void INativeObjectRegistry.Unbind(string objName)
        {
            using (var context = CefV8Context.GetCurrentContext().EnterOrFail())
            {
                DeleteNativeObject(objName, context.V8Context);
            }
        }
    }
}
