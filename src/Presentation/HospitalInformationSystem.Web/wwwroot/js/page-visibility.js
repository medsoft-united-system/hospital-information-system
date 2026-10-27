export function subscribe(receiver) {
  let disposed = false;
  const onVisibilityChanged = () => {
    if (!disposed && document.visibilityState === 'visible') {
      receiver.invokeMethodAsync('OnVisibilityChangedAsync').catch(() => {});
    }
  };
  document.addEventListener('visibilitychange', onVisibilityChanged);
  return {
    dispose() {
      disposed = true;
      document.removeEventListener('visibilitychange', onVisibilityChanged);
    }
  };
}
