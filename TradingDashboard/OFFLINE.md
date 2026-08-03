# Running Offline

Download Chart.js locally:
  curl -o chart.min.js https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.min.js

Then in index.html replace:
  <script src="https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.min.js"></script>
with:
  <script src="chart.min.js"></script>
