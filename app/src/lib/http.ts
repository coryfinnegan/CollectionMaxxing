// The one fetch wrapper the generated client uses (see orval.config.ts).
// The base URL comes from EXPO_PUBLIC_API_URL; failures throw an ApiError that carries Problem Details.

export type ProblemDetails = {
  title?: string | null;
  detail?: string | null;
  status?: number | null;
  errors?: Record<string, string[]>;
};

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.title ?? `Request failed (${status})`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

export const apiBaseUrl = (): string => process.env.EXPO_PUBLIC_API_URL ?? 'http://localhost:5480';

export const customFetch = async <T>(url: string, options: RequestInit = {}): Promise<T> => {
  const response = await fetch(`${apiBaseUrl()}${url}`, {
    ...options,
    headers: {
      Accept: 'application/json',
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...options.headers,
    },
  });
  const text = await response.text();
  const body = text ? JSON.parse(text) : undefined;
  if (!response.ok) {
    throw new ApiError(response.status, (body ?? {}) as ProblemDetails);
  }
  return { data: body, status: response.status, headers: response.headers } as T;
};
