import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CssBaseline, ThemeProvider } from '@mui/material';
import { AuthProvider } from './auth';
import { App } from './App';
import { makeTheme } from './theme';

const qc = new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } } });
const theme = makeTheme(null, 'light');

// a drawing opened in its own tab: its details and the viewer code load while sign-in is checked, not one after the other
if (window.location.pathname.startsWith('/drawings/view/')) {
  const id = window.location.pathname.split('/').pop()!;
  void import('./sequfloh/Drawings');
  fetch(`/api/drawings/${id}`, { credentials: 'same-origin' }).then((r) => (r.ok ? r.json() : null)).then((d) => { if (d && !qc.getQueryData(['drawing', id])) qc.setQueryData(['drawing', id], d); }).catch(() => undefined);
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={qc}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        <BrowserRouter>
          <AuthProvider>
            <App />
          </AuthProvider>
        </BrowserRouter>
      </ThemeProvider>
    </QueryClientProvider>
  </React.StrictMode>,
);
