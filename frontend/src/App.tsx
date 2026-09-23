import { NavLink, Navigate, Route, Routes } from 'react-router-dom';
import { MonitorRoute } from './routes/MonitorRoute.tsx';
import { SimulatorRoute } from './routes/SimulatorRoute.tsx';

export function App() {
  return (
    <div className="app">
      <nav className="nav" aria-label="Main">
        <span className="nav__brand">
          <span className="nav__mark" aria-hidden="true" />
          Financial Monitor
        </span>

        <div className="nav__links">
          <NavLink to="/monitor" className="nav__link">
            Dashboard
          </NavLink>
          <NavLink to="/add" className="nav__link">
            Simulator
          </NavLink>
        </div>
      </nav>

      <main className="main">
        <Routes>
          <Route path="/monitor" element={<MonitorRoute />} />
          <Route path="/add" element={<SimulatorRoute />} />
          {/* The dashboard is what this application is for; anything unrecognised lands there. */}
          <Route path="*" element={<Navigate to="/monitor" replace />} />
        </Routes>
      </main>
    </div>
  );
}
