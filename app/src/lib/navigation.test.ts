import { goBackOr } from './navigation';

const routerWith = (canGoBack: boolean) => ({ canGoBack: jest.fn(() => canGoBack), back: jest.fn(), replace: jest.fn() });

describe('goBackOr', () => {
  it('goes back when there is somewhere to go back to', () => {
    const router = routerWith(true);

    goBackOr(router, '/library');

    expect(router.back).toHaveBeenCalledTimes(1);
    expect(router.replace).not.toHaveBeenCalled();
  });

  it('lands on the fallback when the page was opened fresh and has no history', () => {
    const router = routerWith(false);

    goBackOr(router, '/library');

    expect(router.replace).toHaveBeenCalledWith('/library');
    expect(router.back).not.toHaveBeenCalled();
  });
});
